namespace SeatHive.Api.Services
{
    public interface IRedisLockService
    {
        Task<bool> AcquireLockAsync(string key, TimeSpan expiry);
        Task ReleaseLockAsync(string key);
    }
}