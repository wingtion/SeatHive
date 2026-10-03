using StackExchange.Redis;

namespace SeatHive.Api.Services
{
    public class RedisLockService : IRedisLockService
    {
        // Delete the key only if it still holds our token.
        private const string ReleaseScript =
            "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

        private readonly IConnectionMultiplexer _redis;
        private readonly ILogger<RedisLockService> _logger;

        public RedisLockService(IConnectionMultiplexer redis, ILogger<RedisLockService> logger)
        {
            _redis = redis;
            _logger = logger;
        }

        public async Task<IAsyncDisposable?> AcquireLockAsync(string key, TimeSpan expiry)
        {
            var db = _redis.GetDatabase();
            var token = Guid.NewGuid().ToString("N");

            bool acquired;
            try
            {
                // "StringSet" with "When.NotExists" is the atomic way to grab a lock.
                // If the key already exists, it returns False.
                acquired = await db.StringSetAsync(key, token, expiry, When.NotExists);
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
            {
                throw new LockUnavailableException($"The lock '{key}' could not be acquired: Redis did not answer.", ex);
            }

            return acquired ? new LockHandle(db, key, token, _logger) : null;
        }

        private sealed class LockHandle : IAsyncDisposable
        {
            private readonly IDatabase _db;
            private readonly string _key;
            private readonly string _token;
            private readonly ILogger _logger;
            private int _released;

            public LockHandle(IDatabase db, string key, string token, ILogger logger)
            {
                _db = db;
                _key = key;
                _token = token;
                _logger = logger;
            }

            // Best effort: a lock that is not released runs out by itself. A failure here must never replace
            // the outcome of the work done under the lock, so it is logged and nothing is thrown.
            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _released, 1) == 1) return;

                try
                {
                    await _db.ScriptEvaluateAsync(ReleaseScript, new RedisKey[] { _key }, new RedisValue[] { _token });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "The lock {LockKey} could not be released; it runs out by itself.", _key);
                }
            }
        }
    }
}
