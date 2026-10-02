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

            // Only a lock we actually acquired is released, when the handle is disposed.
            await using var lockHandle = await _lockService.AcquireLockAsync(lockKey, TimeSpan.FromSeconds(10));
            if (lockHandle == null) return "System busy.";

            var seat = await _context.Seats.FindAsync(request.SeatId);
            if (seat == null) return "Seat not found.";
            if (seat.IsBooked) return "Seat is already booked.";

            // Book it, but only if nobody else did since we read it.
            // The database is the second guard in case the lock fails.
            var updatedRows = await _context.Seats
                .Where(s => s.Id == request.SeatId && !s.IsBooked)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(s => s.IsBooked, true)
                    .SetProperty(s => s.UserId, request.UserId)
                    .SetProperty(s => s.Version, s => s.Version + 1));

            if (updatedRows == 0) return "Seat is already booked.";

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
    }
}