using StackExchange.Redis;

namespace SeatHive.Api.Services
{
    public class RedisLockService : IRedisLockService
    {
        private readonly IConnectionMultiplexer _redis;

        public RedisLockService(IConnectionMultiplexer redis)
        {
            _redis = redis;
        }

        public async Task<bool> AcquireLockAsync(string key, TimeSpan expiry)
        {
            var db = _redis.GetDatabase();
            // "StringSet" with "When.NotExists" is the atomic way to grab a lock.
            // If the key already exists, it returns False.
            return await db.StringSetAsync(key, "locked", expiry, When.NotExists);
        }

        public async Task ReleaseLockAsync(string key)
        {
            var db = _redis.GetDatabase();
            await db.KeyDeleteAsync(key);
        }
    }
}