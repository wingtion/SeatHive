using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SeatHive.Api.Services;
using StackExchange.Redis;

namespace SeatHive.Tests.Integration
{
    [Collection(TestCollections.HoldService)]
    [Trait(TestCategories.Trait, TestCategories.Integration)]
    public class RedisLockServiceTests
    {
        // Nothing listens on this port.
        public const string UnreachableRedis = "127.0.0.1:1,connectTimeout=1000";

        private readonly ContainersFixture _fixture;
        private readonly RedisLockService _clientA;
        private readonly RedisLockService _clientB;
        private readonly string _key = $"lock:test:{Guid.NewGuid()}";

        public RedisLockServiceTests(ContainersFixture fixture)
        {
            _fixture = fixture;
            _clientA = CreateService(fixture.Redis);
            _clientB = CreateService(fixture.Redis);
        }

        private static RedisLockService CreateService(IConnectionMultiplexer redis)
        {
            return new RedisLockService(redis, NullLogger<RedisLockService>.Instance);
        }

        // A connection configured the way the API configures its own.
        private static Task<ConnectionMultiplexer> ConnectAsync(string connectionString)
        {
            return ConnectionMultiplexer.ConnectAsync(RedisSetup.CreateOptions(connectionString));
        }

        // ---- When Redis cannot be reached ----

        // The caller is told that nobody can say who holds the lock, and quickly: a request should not wait
        // for a Redis that is down.
        [Fact]
        public async Task Acquire_ShouldFailFastWithLockUnavailable_WhenRedisCannotBeReached()
        {
            await using var redis = await ConnectAsync(UnreachableRedis);
            var service = CreateService(redis);

            var watch = Stopwatch.StartNew();
            var error = await Assert.ThrowsAsync<LockUnavailableException>(() => service.AcquireLockAsync(_key, TimeSpan.FromSeconds(10)));
            watch.Stop();

            Assert.IsAssignableFrom<RedisException>(error.InnerException);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Took {watch.Elapsed.TotalMilliseconds:F0} ms.");
        }

        // Releasing is best effort: the lock runs out by itself. A failed release must never replace
        // the outcome of the work that was done under the lock.
        [Fact]
        public async Task Release_ShouldNotThrow_WhenRedisIsGoneByThen()
        {
            var redis = await ConnectAsync(_fixture.RedisConnectionString);
            var handle = await CreateService(redis).AcquireLockAsync(_key, TimeSpan.FromSeconds(30));
            Assert.NotNull(handle);
            await redis.CloseAsync();
            redis.Dispose();

            await handle.DisposeAsync();

            // Not released, so it stays until it runs out.
            Assert.True(await _fixture.Redis.GetDatabase().KeyExistsAsync(_key));
        }

        // ---- With Redis ----

        [Fact]
        public async Task Acquire_ByClientThatDidNotGetLock_ShouldReturnNullAndLeaveLockInPlace()
        {
            var handleA = await _clientA.AcquireLockAsync(_key, TimeSpan.FromSeconds(30));
            Assert.NotNull(handleA);

            // The loser gets no handle, so it has nothing it could release.
            var handleB = await _clientB.AcquireLockAsync(_key, TimeSpan.FromSeconds(30));

            Assert.Null(handleB);
            Assert.True(await _fixture.Redis.GetDatabase().KeyExistsAsync(_key));
        }

        [Fact]
        public async Task Release_AfterOwnLockExpired_ShouldNotDeleteNewOwnersLock()
        {
            var handleA = await _clientA.AcquireLockAsync(_key, TimeSpan.FromMilliseconds(200));
            Assert.NotNull(handleA);
            await Task.Delay(500);
            var handleB = await _clientB.AcquireLockAsync(_key, TimeSpan.FromSeconds(30));
            Assert.NotNull(handleB);

            // A's token no longer matches the value in Redis.
            await handleA.DisposeAsync();

            Assert.True(await _fixture.Redis.GetDatabase().KeyExistsAsync(_key));
        }

        [Fact]
        public async Task Release_ByOwner_ShouldDeleteLock()
        {
            var handleA = await _clientA.AcquireLockAsync(_key, TimeSpan.FromSeconds(30));
            Assert.NotNull(handleA);

            await handleA.DisposeAsync();

            Assert.False(await _fixture.Redis.GetDatabase().KeyExistsAsync(_key));
        }

        [Fact]
        public async Task Release_CalledTwice_ShouldNotDeleteNewOwnersLock()
        {
            var handleA = await _clientA.AcquireLockAsync(_key, TimeSpan.FromSeconds(30));
            Assert.NotNull(handleA);
            await handleA.DisposeAsync();
            var handleB = await _clientB.AcquireLockAsync(_key, TimeSpan.FromSeconds(30));
            Assert.NotNull(handleB);

            await handleA.DisposeAsync();

            Assert.True(await _fixture.Redis.GetDatabase().KeyExistsAsync(_key));
        }

        [Fact]
        public async Task Acquire_ShouldSucceedAgain_AfterTtlExpires()
        {
            Assert.NotNull(await _clientA.AcquireLockAsync(_key, TimeSpan.FromMilliseconds(200)));
            await Task.Delay(500);

            Assert.NotNull(await _clientB.AcquireLockAsync(_key, TimeSpan.FromSeconds(30)));
        }
    }
}
