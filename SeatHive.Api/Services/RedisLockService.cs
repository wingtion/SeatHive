using StackExchange.Redis;

namespace SeatHive.Api.Services
{
    public class RedisLockService : IRedisLockService
    {
        // Delete the key only if it still holds our token.
        private const string ReleaseScript =
            "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

        private readonly IConnectionMultiplexer _redis;

        public RedisLockService(IConnectionMultiplexer redis)
        {
            _redis = redis;
        }

        public async Task<IAsyncDisposable?> AcquireLockAsync(string key, TimeSpan expiry)
        {
            var db = _redis.GetDatabase();
            var token = Guid.NewGuid().ToString("N");

            // "StringSet" with "When.NotExists" is the atomic way to grab a lock.
            // If the key already exists, it returns False.
            var acquired = await db.StringSetAsync(key, token, expiry, When.NotExists);
            return acquired ? new LockHandle(db, key, token) : null;
        }

        private sealed class LockHandle : IAsyncDisposable
        {
            private readonly IDatabase _db;
            private readonly string _key;
            private readonly string _token;
            private int _released;

            public LockHandle(IDatabase db, string key, string token)
            {
                _db = db;
                _key = key;
                _token = token;
            }

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _released, 1) == 1) return;

                await _db.ScriptEvaluateAsync(ReleaseScript, new RedisKey[] { _key }, new RedisValue[] { _token });
            }
        }
    }
}
