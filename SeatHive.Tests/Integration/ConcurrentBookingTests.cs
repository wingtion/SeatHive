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

            for (var round = 1; round <= Rounds; round++)
            {
                var seatId = await _fixture.CreateFreeSeatAsync();
                var bus = new Mock<IPublishEndpoint>();

                var attempts = Enumerable.Range(1, ConcurrentRequests).Select(userId => Task.Run(async () =>
                {
                    await using var db = _fixture.CreateContext();
                    var service = new BookingService(db, new RedisLockService(_fixture.Redis), bus.Object);
                    var result = await service.BookSeatAsync(seatId, userId);
                    return (userId, result);
                }));

                var results = await Task.WhenAll(attempts);
                var winners = results.Where(r => r.result == "Booking successful!").Select(r => r.userId).ToList();
                var published = bus.Invocations.Count(i => i.Method.Name == nameof(IPublishEndpoint.Publish));

                await using var verifyDb = _fixture.CreateContext();
                var seat = await verifyDb.Seats.AsNoTracking().SingleAsync(s => s.Id == seatId);

                if (winners.Count != 1 || published != 1 || seat.UserId != winners[0])
                {
                    failedRounds.Add(
                        $"round {round}: successes={winners.Count} [{string.Join(",", winners)}], " +
                        $"events={published}, seat.UserId={seat.UserId}");
                }
            }

            Assert.True(failedRounds.Count == 0,
                $"{failedRounds.Count}/{Rounds} rounds broke the one-winner guarantee:\n" + string.Join("\n", failedRounds));
        }
    }
}
