using SeatHive.Api.Services;

namespace SeatHive.Tests.Integration
{
    [Collection(ContainersCollection.Name)]
    public class RedisLockServiceTests
    {
        private readonly ContainersFixture _fixture;
        private readonly RedisLockService _clientA;
        private readonly RedisLockService _clientB;
        private readonly string _key = $"lock:test:{Guid.NewGuid()}";

        public RedisLockServiceTests(ContainersFixture fixture)
        {
            _fixture = fixture;
            _clientA = new RedisLockService(fixture.Redis);
            _clientB = new RedisLockService(fixture.Redis);
        }

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
