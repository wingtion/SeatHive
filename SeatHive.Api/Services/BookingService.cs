using MassTransit;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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

        public async Task<BookingResult> BookSeatAsync(int seatId, int userId)
        {
            var lockKey = $"lock:seat:{seatId}";

            // The Redis lock only keeps concurrent requests for one seat apart.
            // Only a lock we actually acquired is released, when the handle is disposed.
            await using var lockHandle = await _lockService.AcquireLockAsync(lockKey, TimeSpan.FromSeconds(10));
            if (lockHandle == null) return BookingResult.Failure(BookingError.SeatLocked);

            if (!await _context.Seats.AnyAsync(s => s.Id == seatId))
                return BookingResult.Failure(BookingError.SeatNotFound);

            var isTaken = await _context.Bookings.AnyAsync(b =>
                b.SeatId == seatId && (b.Status == BookingStatus.Held || b.Status == BookingStatus.Confirmed));
            if (isTaken) return BookingResult.Failure(BookingError.SeatAlreadyBooked);

            // Book it. There is no hold step yet, so the booking is confirmed immediately.
            var now = DateTime.UtcNow;
            var booking = new Booking
            {
                SeatId = seatId,
                UserId = userId,
                Status = BookingStatus.Confirmed,
                CreatedAt = now,
                ConfirmedAt = now
            };
            _context.Bookings.Add(booking);

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // The database is the source of truth: its unique index allows one active booking per seat.
                // We get here if someone else booked the seat since we checked, for example because the lock failed.
                return BookingResult.Failure(BookingError.SeatAlreadyBooked);
            }

            // 3. Publish event to rabbitmq
            // using an anonymous object that matches the interface
            await _publishEndpoint.Publish<BookingCreatedEvent>(new
            {
                SeatId = seatId,
                UserId = userId,
                CreatedAt = now
            });

            return BookingResult.Success(booking);
        }
    }
}
