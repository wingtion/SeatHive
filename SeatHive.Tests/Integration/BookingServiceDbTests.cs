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
    [Collection(TestCollections.PaymentService)]
    [Trait(TestCategories.Trait, TestCategories.Integration)]
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
        public async Task HoldPayConfirm_ShouldConfirmTheBooking_AndPublishEachStepOnce()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var userId = await _fixture.CreateUserAsync();
            await using var db = _fixture.CreateContext();
            var service = _fixture.CreateBookingService(db, _clock, _mockLock.Object, _mockBus.Object);

            var hold = await service.HoldSeatAsync(seatId, userId);

            Assert.True(hold.IsSuccess);
            Assert.Equal(BookingStatus.Held, hold.Booking!.Status);
            Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddMinutes(5), hold.Booking.ExpiresAt);
            var held = Assert.Single(ContainersFixture.PublishedTo<SeatHeld>(_mockBus));
            Assert.Equal((hold.Booking.Id, seatId, userId), (held.BookingId, held.SeatId, held.UserId));
            Assert.Equal(hold.Booking.ExpiresAt, held.ExpiresAt);

            var payment = await service.RequestPaymentAsync(hold.Booking.Id, userId);

            Assert.True(payment.IsSuccess);
            var requested = Assert.Single(ContainersFixture.PublishedTo<PaymentRequested>(_mockBus));
            Assert.Equal((hold.Booking.Id, seatId, userId), (requested.BookingId, requested.SeatId, requested.UserId));

            var confirm = await service.CompletePaymentAsync(ContainersFixture.SuccessfulPayment(hold.Booking.Id, requested.PaymentId));

            Assert.True(confirm.IsSuccess);
            var confirmed = Assert.Single(ContainersFixture.PublishedTo<BookingConfirmed>(_mockBus));
            Assert.Equal((hold.Booking.Id, seatId, userId, requested.PaymentId),
                (confirmed.BookingId, confirmed.SeatId, confirmed.UserId, confirmed.PaymentId));
            // Three steps, three events.
            Assert.Equal(3, _mockBus.Invocations.Count);

            await using var verifyDb = _fixture.CreateContext();
            var booking = await verifyDb.Bookings.AsNoTracking().SingleAsync(b => b.SeatId == seatId);
            Assert.Equal(hold.Booking.Id, booking.Id);
            Assert.Equal(userId, booking.UserId);
            Assert.Equal(BookingStatus.Confirmed, booking.Status);
            Assert.Equal(_clock.GetUtcNow().UtcDateTime, booking.ConfirmedAt);
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
