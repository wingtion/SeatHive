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

            var userId = await _fixture.CreateUserAsync();

            var result = await service.BookSeatAsync(seatId, userId);

            Assert.True(result.IsSuccess);

            mockBus.Verify(x => x.Publish<BookingCreatedEvent>(It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);

            await using var verifyDb = _fixture.CreateContext();
            var booking = await verifyDb.Bookings.AsNoTracking().SingleAsync(b => b.SeatId == seatId);
            Assert.Equal(result.Booking!.Id, booking.Id);
            Assert.Equal(userId, booking.UserId);
            Assert.Equal(BookingStatus.Confirmed, booking.Status);
            Assert.NotNull(booking.ConfirmedAt);
            Assert.Null(booking.ExpiresAt);
        }
    }
}
