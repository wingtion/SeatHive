using MassTransit; 
using Microsoft.EntityFrameworkCore;
using SeatHive.Api.Data;
using SeatHive.Api.Models;
using SeatHive.Shared.Events;

namespace SeatHive.Api.Services
{
    public class BookingService
    {
        private readonly AppDbContext _context;
        private readonly IRedisLockService _lockService;
        private readonly IPublishEndpoint _publishEndpoint; 
        public BookingService(AppDbContext context, IRedisLockService lockService, IPublishEndpoint publishEndpoint)
        {
            _context = context;
            _lockService = lockService;
            _publishEndpoint = publishEndpoint;
        }

        public async Task<string> BookSeatAsync(BookingRequest request)
        {
            var lockKey = $"lock:seat:{request.SeatId}";

            try
            {
                var isLocked = await _lockService.AcquireLockAsync(lockKey, TimeSpan.FromSeconds(10));
                if (!isLocked) return "System busy.";

                var seat = await _context.Seats.FindAsync(request.SeatId);
                if (seat == null) return "Seat not found.";
                if (seat.IsBooked) return "Seat is already booked.";

                // Book it
                seat.IsBooked = true;
                seat.UserId = request.UserId;
                seat.Version++;

                await _context.SaveChangesAsync();

                // 3. Publish event to rabbitmq
                // using an anonymous object that matches the interface
                await _publishEndpoint.Publish<BookingCreatedEvent>(new
                {
                    SeatId = seat.Id,
                    UserId = request.UserId,
                    CreatedAt = DateTime.UtcNow
                });

                return "Booking successful!";
            }
            finally
            {
                await _lockService.ReleaseLockAsync(lockKey);
            }
        }
    }
}