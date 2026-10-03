namespace SeatHive.Api.Services
{
    public interface IRedisLockService
    {
        // Returns a handle that releases the lock when disposed, or null if the lock is held by someone else.
        // Throws LockUnavailableException when the lock store cannot be reached, so nobody knows who holds it.
        Task<IAsyncDisposable?> AcquireLockAsync(string key, TimeSpan expiry);
    }

    // The lock store (Redis) could not be asked. Whether to go on without the lock is up to the caller.
    public class LockUnavailableException : Exception
    {
        public LockUnavailableException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
