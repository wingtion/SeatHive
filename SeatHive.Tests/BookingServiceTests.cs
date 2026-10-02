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

        [Fact]
        public async Task BookSeat_ShouldReturnError_WhenSeatIsAlreadyBooked()
        {
            var seatId = 1;
            _fakeDb.Seats.Add(new Seat { Id = seatId, IsBooked = true, UserId = 99 });
            await _fakeDb.SaveChangesAsync();

            var result = await _service.BookSeatAsync(seatId, 100);

            Assert.Equal("Seat is already booked.", result);
        }

        [Fact]
        public async Task BookSeat_ShouldNotReleaseLock_WhenLockIsNotAcquired()
        {
            _mockLock.Setup(x => x.AcquireLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()))
                     .ReturnsAsync((IAsyncDisposable?)null);

            var result = await _service.BookSeatAsync(3, 100);

            Assert.Equal("System busy.", result);
            _mockLockHandle.Verify(x => x.DisposeAsync(), Times.Never);
        }

        [Fact]
        public async Task BookSeat_ShouldReleaseLockOnce_WhenLockIsAcquired()
        {
            _fakeDb.Seats.Add(new Seat { Id = 4, IsBooked = true, UserId = 99 });
            await _fakeDb.SaveChangesAsync();

            await _service.BookSeatAsync(4, 100);

            _mockLockHandle.Verify(x => x.DisposeAsync(), Times.Once);
        }
    }
}