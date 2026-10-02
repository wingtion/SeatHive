using MassTransit;
using Microsoft.EntityFrameworkCore;
using Moq;
using SeatHive.Api.Models;
using SeatHive.Api.Services;
using SeatHive.Shared.Events;

namespace SeatHive.Tests.Integration
{
    [Collection(ContainersCollection.Name)]
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
        public async Task ConcurrentRequestsForSameSeat_ShouldProduceExactlyOneBooking()
        {
            var failedRounds = new List<string>();
            var users = await _fixture.CreateUsersAsync(ConcurrentRequests);

            for (var round = 1; round <= Rounds; round++)
            {
                var seatId = await _fixture.CreateFreeSeatAsync();
                var bus = new Mock<IPublishEndpoint>();

                var attempts = users.Select(userId => Task.Run(async () =>
                {
                    await using var db = _fixture.CreateContext();
                    var service = new BookingService(db, new RedisLockService(_fixture.Redis), bus.Object);
                    var result = await service.BookSeatAsync(seatId, userId);
                    return (userId, result);
                }));

                var results = await Task.WhenAll(attempts);
                var winners = results.Where(r => r.result.IsSuccess).Select(r => r.userId).ToList();
                var published = bus.Invocations.Count(i => i.Method.Name == nameof(IPublishEndpoint.Publish));

                // The seat must have exactly one booking, and it must belong to the winner.
                await using var verifyDb = _fixture.CreateContext();
                var bookedBy = await verifyDb.Bookings.AsNoTracking()
                    .Where(b => b.SeatId == seatId)
                    .Select(b => b.UserId)
                    .ToListAsync();

                if (winners.Count != 1 || published != 1 || bookedBy.Count != 1 || bookedBy[0] != winners[0])
                {
                    failedRounds.Add(
                        $"round {round}: successes={winners.Count} [{string.Join(",", winners)}], " +
                        $"events={published}, bookings by=[{string.Join(",", bookedBy)}]");
                }
            }

            Assert.True(failedRounds.Count == 0,
                $"{failedRounds.Count}/{Rounds} rounds broke the one-winner guarantee:\n" + string.Join("\n", failedRounds));
        }
    }
}
