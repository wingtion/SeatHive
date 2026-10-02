using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Moq;
using SeatHive.Api.Models;
using SeatHive.Api.Services;
using SeatHive.Shared.Events;

namespace SeatHive.Tests.Integration
{
    // Runs on real Postgres because the InMemory provider does not support ExecuteUpdateAsync or transactions.
    [Collection(ContainersCollection.Name)]
    public class BookingServiceDbTests
    {
        private readonly ContainersFixture _fixture;
        private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        private readonly Mock<IPublishEndpoint> _mockBus = new();
        private readonly Mock<IRedisLockService> _mockLock = new();
        private readonly Mock<IAsyncDisposable> _mockLockHandle = new();

        public BookingServiceDbTests(ContainersFixture fixture)
        {
            _fixture = fixture;

            _mockLock.Setup(x => x.AcquireLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()))
                     .ReturnsAsync(_mockLockHandle.Object);
        }

        private async Task AddBookingAsync(int seatId, int userId, BookingStatus status)
        {
            await using var db = _fixture.CreateContext();
            db.Bookings.Add(new Booking
            {
                SeatId = seatId,
                UserId = userId,
                Status = status,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
                ExpiresAt = status == BookingStatus.Held ? _clock.GetUtcNow().UtcDateTime.AddMinutes(5) : null
            });
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task HoldThenConfirm_ShouldConfirmTheBooking_AndPublishOnce()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var userId = await _fixture.CreateUserAsync();
            await using var db = _fixture.CreateContext();
            var service = _fixture.CreateBookingService(db, _clock, _mockLock.Object, _mockBus.Object);

            var hold = await service.HoldSeatAsync(seatId, userId);

            Assert.True(hold.IsSuccess);
            Assert.Equal(BookingStatus.Held, hold.Booking!.Status);
            Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddMinutes(5), hold.Booking.ExpiresAt);
            // A hold is not a booking yet.
            Assert.Empty(_mockBus.Invocations);

            var confirm = await service.ConfirmAsync(hold.Booking.Id, userId);

            Assert.True(confirm.IsSuccess);
            _mockBus.Verify(x => x.Publish<BookingCreatedEvent>(It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);

            await using var verifyDb = _fixture.CreateContext();
            var booking = await verifyDb.Bookings.AsNoTracking().SingleAsync(b => b.SeatId == seatId);
            Assert.Equal(hold.Booking.Id, booking.Id);
            Assert.Equal(userId, booking.UserId);
            Assert.Equal(BookingStatus.Confirmed, booking.Status);
            Assert.Equal(_clock.GetUtcNow().UtcDateTime, booking.ConfirmedAt);
        }

        [Fact]
        public async Task Confirm_ShouldPublishOnlyOnce_WhenRepeated()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var userId = await _fixture.CreateUserAsync();
            await using var db = _fixture.CreateContext();
            var service = _fixture.CreateBookingService(db, _clock, _mockLock.Object, _mockBus.Object);
            var hold = await service.HoldSeatAsync(seatId, userId);

            var first = await service.ConfirmAsync(hold.Booking!.Id, userId);
            var second = await service.ConfirmAsync(hold.Booking.Id, userId);

            Assert.True(first.IsSuccess);
            Assert.True(second.IsSuccess);
            Assert.False(second.Changed);
            Assert.Equal(BookingStatus.Confirmed, second.Booking!.Status);
            _mockBus.Verify(x => x.Publish<BookingCreatedEvent>(It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Hold_ShouldReturnSeatHeld_WhenAnotherUserHoldsTheSeat()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var users = await _fixture.CreateUsersAsync(2);
            await AddBookingAsync(seatId, users[0], BookingStatus.Held);
            await using var db = _fixture.CreateContext();
            var service = _fixture.CreateBookingService(db, _clock, _mockLock.Object, _mockBus.Object);

            var result = await service.HoldSeatAsync(seatId, users[1]);

            Assert.Equal(BookingError.SeatHeld, result.Error);
            Assert.Null(result.Booking);
        }

        [Fact]
        public async Task Hold_ShouldReturnSeatAlreadyBooked_WhenSeatIsConfirmed()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var users = await _fixture.CreateUsersAsync(2);
            await AddBookingAsync(seatId, users[0], BookingStatus.Confirmed);
            await using var db = _fixture.CreateContext();
            var service = _fixture.CreateBookingService(db, _clock, _mockLock.Object, _mockBus.Object);

            var result = await service.HoldSeatAsync(seatId, users[1]);

            Assert.Equal(BookingError.SeatAlreadyBooked, result.Error);
            Assert.Null(result.Booking);
        }

        [Theory]
        [InlineData(BookingStatus.Expired)]
        [InlineData(BookingStatus.Released)]
        public async Task Hold_ShouldSucceed_WhenSeatOnlyHasInactiveBookings(BookingStatus inactiveStatus)
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var users = await _fixture.CreateUsersAsync(2);
            await AddBookingAsync(seatId, users[0], inactiveStatus);
            await using var db = _fixture.CreateContext();
            var service = _fixture.CreateBookingService(db, _clock, _mockLock.Object, _mockBus.Object);

            var result = await service.HoldSeatAsync(seatId, users[1]);

            Assert.True(result.IsSuccess);
            Assert.Equal(BookingStatus.Held, result.Booking!.Status);
            Assert.Equal(users[1], result.Booking.UserId);
        }

        [Fact]
        public async Task Hold_ShouldReturnError_AndPublishNothing_WhenSeatDoesNotExist()
        {
            var userId = await _fixture.CreateUserAsync();
            await using var db = _fixture.CreateContext();
            var service = _fixture.CreateBookingService(db, _clock, _mockLock.Object, _mockBus.Object);

            var result = await service.HoldSeatAsync(int.MaxValue, userId);

            Assert.Equal(BookingError.SeatNotFound, result.Error);
            Assert.Empty(_mockBus.Invocations);
        }

        [Fact]
        public async Task Hold_ShouldNotReleaseLock_WhenLockIsNotAcquired()
        {
            _mockLock.Setup(x => x.AcquireLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()))
                     .ReturnsAsync((IAsyncDisposable?)null);
            var seatId = await _fixture.CreateFreeSeatAsync();
            var userId = await _fixture.CreateUserAsync();
            await using var db = _fixture.CreateContext();
            var service = _fixture.CreateBookingService(db, _clock, _mockLock.Object, _mockBus.Object);

            var result = await service.HoldSeatAsync(seatId, userId);

            Assert.Equal(BookingError.SeatLocked, result.Error);
            _mockLockHandle.Verify(x => x.DisposeAsync(), Times.Never);
        }

        [Fact]
        public async Task Hold_ShouldReleaseLockOnce_WhenLockIsAcquired()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var userId = await _fixture.CreateUserAsync();
            await using var db = _fixture.CreateContext();
            var service = _fixture.CreateBookingService(db, _clock, _mockLock.Object, _mockBus.Object);

            await service.HoldSeatAsync(seatId, userId);

            _mockLockHandle.Verify(x => x.DisposeAsync(), Times.Once);
        }
    }
}
