using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using SeatHive.Api.Models;
using SeatHive.Api.Services;
using SeatHive.Shared.Events;
using StackExchange.Redis;

namespace SeatHive.Tests.Integration
{
    [Collection(TestCollections.HoldService)]
    [Trait(TestCategories.Trait, TestCategories.Integration)]
    public class ConcurrentBookingTests
    {
        private const int ConcurrentRequests = 20;
        private const int Rounds = 25;

        private readonly ContainersFixture _fixture;

        public ConcurrentBookingTests(ContainersFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task ConcurrentHoldsForSameSeat_ShouldProduceExactlyOneHold()
        {
            await AssertOneWinnerPerRoundAsync(lockService: null, losersError: null);
        }

        // Without Redis the holds go on without the lock, and the database alone decides: still exactly one hold,
        // and everyone else is told that the seat is held (the lock, which would say "locked", is not there).
        // The database turns the others away without a failed command: losing a race is not an error.
        [Fact]
        public async Task ConcurrentHoldsForSameSeat_ShouldProduceExactlyOneHold_WhenRedisIsDown()
        {
            await using var redis = await ConnectionMultiplexer.ConnectAsync(RedisSetup.CreateOptions(RedisLockServiceTests.UnreachableRedis));
            var lockService = new RedisLockService(redis, NullLogger<RedisLockService>.Instance);

            await AssertOneWinnerPerRoundAsync(lockService, losersError: BookingError.SeatHeld);
        }

        // lockService null: the real lock on the test Redis. losersError null: any failure counts as losing.
        private async Task AssertOneWinnerPerRoundAsync(IRedisLockService? lockService, BookingError? losersError)
        {
            var failedRounds = new List<string>();
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var failedCommands = new FailedCommandCounter();

            for (var round = 1; round <= Rounds; round++)
            {
                // New users every round: winners keep their hold, and a user at the hold limit could not win again.
                var users = await _fixture.CreateUsersAsync(ConcurrentRequests);
                var seatId = await _fixture.CreateFreeSeatAsync();
                var bus = new Mock<IPublishEndpoint>();

                var attempts = users.Select(userId => Task.Run(async () =>
                {
                    await using var db = _fixture.CreateContext(failedCommands);
                    var service = _fixture.CreateBookingService(db, clock, lockService, bus.Object);
                    var result = await service.HoldSeatAsync(seatId, userId);
                    return (userId, result);
                }));

                var results = await Task.WhenAll(attempts);
                var winners = results.Where(r => r.result.IsSuccess).Select(r => r.userId).ToList();
                var wrongErrors = results
                    .Where(r => !r.result.IsSuccess && losersError != null && r.result.Error != losersError)
                    .Select(r => r.result.Error)
                    .ToList();
                var published = ContainersFixture.PublishedTo<SeatHeld>(bus);

                // The seat must have exactly one booking, a hold, and it must belong to the winner.
                await using var verifyDb = _fixture.CreateContext();
                var bookings = await verifyDb.Bookings.AsNoTracking()
                    .Where(b => b.SeatId == seatId)
                    .ToListAsync();

                // Only the winner announces a held seat.
                if (winners.Count != 1 || published.Count != 1 || published[0].UserId != winners[0] || bookings.Count != 1
                    || bookings[0].UserId != winners[0] || bookings[0].Status != BookingStatus.Held || wrongErrors.Count != 0)
                {
                    failedRounds.Add(
                        $"round {round}: successes={winners.Count} [{string.Join(",", winners)}], events={published.Count}, " +
                        $"bookings=[{string.Join(",", bookings.Select(b => $"{b.UserId}:{b.Status}"))}], " +
                        $"unexpected errors=[{string.Join(",", wrongErrors)}]");
                }
            }

            Assert.True(failedRounds.Count == 0,
                $"{failedRounds.Count}/{Rounds} rounds broke the one-winner guarantee:\n" + string.Join("\n", failedRounds));
            Assert.True(failedCommands.Failures.Count == 0,
                $"{failedCommands.Failures.Count} database commands failed, for example: {failedCommands.Failures.FirstOrDefault()}");
        }
    }
}
