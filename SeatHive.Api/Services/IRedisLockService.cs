namespace SeatHive.Api.Services
{
    public interface IRedisLockService
    {
        // Returns a handle that releases the lock when disposed, or null if the lock is held by someone else.
        Task<IAsyncDisposable?> AcquireLockAsync(string key, TimeSpan expiry);
    }
}
