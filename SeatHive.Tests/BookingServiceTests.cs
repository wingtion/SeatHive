using MassTransit;
using Microsoft.EntityFrameworkCore;
using Moq;
using SeatHive.Api.Data;
using SeatHive.Api.Models;
using SeatHive.Api.Services;

namespace SeatHive.Tests
{
    public class BookingServiceTests
    {
        private readonly AppDbContext _fakeDb;
        private readonly Mock<IPublishEndpoint> _mockBus;
        private readonly Mock<IRedisLockService> _mockLock; // <--- Mock the Interface
        private readonly Mock<IAsyncDisposable> _mockLockHandle;
        private readonly BookingService _service;

        public BookingServiceTests()
        {
            // 1. Setup Fake Database
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
            _fakeDb = new AppDbContext(options);

            // 2. Setup Fake RabbitMQ
            _mockBus = new Mock<IPublishEndpoint>();

            // 3. Setup Fake Lock Service
            _mockLock = new Mock<IRedisLockService>();
            _mockLockHandle = new Mock<IAsyncDisposable>();

            _mockLock.Setup(x => x.AcquireLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()))
                     .ReturnsAsync(_mockLockHandle.Object);

            // 4. Create Service
            _service = new BookingService(_fakeDb, _mockLock.Object, _mockBus.Object);
        }

        [Theory]
        [InlineData(BookingStatus.Confirmed)]
        [InlineData(BookingStatus.Held)]
        public async Task BookSeat_ShouldReturnError_WhenSeatHasAnActiveBooking(BookingStatus activeStatus)
        {
            var seatId = 1;
            _fakeDb.Seats.Add(new Seat { Id = seatId });
            _fakeDb.Bookings.Add(new Booking { SeatId = seatId, UserId = 99, Status = activeStatus });
            await _fakeDb.SaveChangesAsync();

            var result = await _service.BookSeatAsync(seatId, 100);

            Assert.Equal(BookingError.SeatAlreadyBooked, result.Error);
            Assert.Null(result.Booking);
        }

        [Theory]
        [InlineData(BookingStatus.Expired)]
        [InlineData(BookingStatus.Released)]
        public async Task BookSeat_ShouldSucceed_WhenSeatOnlyHasInactiveBookings(BookingStatus inactiveStatus)
        {
            var seatId = 2;
            _fakeDb.Seats.Add(new Seat { Id = seatId });
            _fakeDb.Bookings.Add(new Booking { SeatId = seatId, UserId = 99, Status = inactiveStatus });
            await _fakeDb.SaveChangesAsync();

            var result = await _service.BookSeatAsync(seatId, 100);

            Assert.True(result.IsSuccess);
            Assert.Equal(BookingStatus.Confirmed, result.Booking!.Status);
            Assert.Equal(100, result.Booking.UserId);
            Assert.NotNull(result.Booking.ConfirmedAt);
        }

        [Fact]
        public async Task BookSeat_ShouldReturnError_AndPublishNothing_WhenSeatDoesNotExist()
        {
            var result = await _service.BookSeatAsync(404, 100);

            Assert.Equal(BookingError.SeatNotFound, result.Error);
            Assert.Empty(_mockBus.Invocations);
        }

        [Fact]
        public async Task BookSeat_ShouldNotReleaseLock_WhenLockIsNotAcquired()
        {
            _mockLock.Setup(x => x.AcquireLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()))
                     .ReturnsAsync((IAsyncDisposable?)null);

            var result = await _service.BookSeatAsync(3, 100);

            Assert.Equal(BookingError.SeatLocked, result.Error);
            _mockLockHandle.Verify(x => x.DisposeAsync(), Times.Never);
        }

        [Fact]
        public async Task BookSeat_ShouldReleaseLockOnce_WhenLockIsAcquired()
        {
            _fakeDb.Seats.Add(new Seat { Id = 4 });
            await _fakeDb.SaveChangesAsync();

            await _service.BookSeatAsync(4, 100);

            _mockLockHandle.Verify(x => x.DisposeAsync(), Times.Once);
        }
    }
}