using MassTransit;
using Microsoft.EntityFrameworkCore;
using Moq;
using SeatHive.Api.Models;
using SeatHive.Api.Services;
using SeatHive.Shared.Events;

namespace SeatHive.Tests.Integration
{
    // Runs on real Postgres because the InMemory provider does not support ExecuteUpdateAsync.
    [Collection(ContainersCollection.Name)]
    public class BookingServiceDbTests
    {
        private readonly ContainersFixture _fixture;

        public BookingServiceDbTests(ContainersFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task BookSeat_ShouldSuccess_WhenSeatIsFree()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var mockBus = new Mock<IPublishEndpoint>();
            var mockLock = new Mock<IRedisLockService>();
            mockLock.Setup(x => x.AcquireLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()))
                    .ReturnsAsync(Mock.Of<IAsyncDisposable>());

            await using var db = _fixture.CreateContext();
            var service = new BookingService(db, mockLock.Object, mockBus.Object);

            var result = await service.BookSeatAsync(new BookingRequest { SeatId = seatId, UserId = 100 });

            Assert.Equal("Booking successful!", result);

            mockBus.Verify(x => x.Publish<BookingCreatedEvent>(It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);

            await using var verifyDb = _fixture.CreateContext();
            var seat = await verifyDb.Seats.AsNoTracking().SingleAsync(s => s.Id == seatId);
            Assert.True(seat.IsBooked);
            Assert.Equal(100, seat.UserId);
            Assert.Equal(2, seat.Version);
        }
    }
}
