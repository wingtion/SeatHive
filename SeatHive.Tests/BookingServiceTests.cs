using MassTransit;
using Microsoft.EntityFrameworkCore;
using Moq;
using SeatHive.Api.Data;
using SeatHive.Api.Models;
using SeatHive.Api.Services;
using SeatHive.Shared.Events;

namespace SeatHive.Tests
{
    public class BookingServiceTests
    {
        private readonly AppDbContext _fakeDb;
        private readonly Mock<IPublishEndpoint> _mockBus;
        private readonly Mock<IRedisLockService> _mockLock; // <--- Mock the Interface
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
            
            _mockLock.Setup(x => x.AcquireLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()))
                     .ReturnsAsync(true);

            // 4. Create Service
            _service = new BookingService(_fakeDb, _mockLock.Object, _mockBus.Object);
        }

        [Fact]
        public async Task BookSeat_ShouldReturnError_WhenSeatIsAlreadyBooked()
        {
            var seatId = 1;
            _fakeDb.Seats.Add(new Seat { Id = seatId, IsBooked = true, UserId = 99 });
            await _fakeDb.SaveChangesAsync();

            var request = new BookingRequest { SeatId = seatId, UserId = 100 };

            var result = await _service.BookSeatAsync(request);

            Assert.Equal("Seat is already booked.", result);
        }

        [Fact]
        public async Task BookSeat_ShouldSuccess_WhenSeatIsFree()
        {
            var seatId = 2;
            _fakeDb.Seats.Add(new Seat { Id = seatId, IsBooked = false });
            await _fakeDb.SaveChangesAsync();

            var request = new BookingRequest { SeatId = seatId, UserId = 100 };

            var result = await _service.BookSeatAsync(request);

            Assert.Equal("Booking successful!", result);

            _mockBus.Verify(x => x.Publish<BookingCreatedEvent>(It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}