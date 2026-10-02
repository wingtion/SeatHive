using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
        private readonly TimeProvider _timeProvider;
        private readonly HoldOptions _options;

        public BookingService(
            AppDbContext context,
            IRedisLockService lockService,
            IPublishEndpoint publishEndpoint,
            TimeProvider timeProvider,
            IOptions<HoldOptions> options)
        {
            _context = context;
            _lockService = lockService;
            _publishEndpoint = publishEndpoint;
            _timeProvider = timeProvider;
            _options = options.Value;
        }

        private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

        public async Task<BookingResult> HoldSeatAsync(int seatId, int userId)
        {
            var now = UtcNow();

            // Holding a seat you already hold returns that hold. It does not extend it.
            var ownHold = await _context.Bookings.AsNoTracking().FirstOrDefaultAsync(b =>
                b.SeatId == seatId && b.UserId == userId && b.Status == BookingStatus.Held && b.ExpiresAt > now);
            if (ownHold != null) return BookingResult.Unchanged(ownHold);

            var lockKey = $"lock:seat:{seatId}";

            // The Redis lock only keeps concurrent requests for one seat apart.
            // Only a lock we actually acquired is released, when the handle is disposed.
            await using var lockHandle = await _lockService.AcquireLockAsync(lockKey, TimeSpan.FromSeconds(10));
            if (lockHandle == null) return BookingResult.Failure(BookingError.SeatLocked);

            // Leaving without a commit rolls everything back.
            await using var transaction = await _context.Database.BeginTransactionAsync();

            // One hold at a time per user, so concurrent requests for different seats cannot pass the limit together.
            await _context.Database.ExecuteSqlAsync($"SELECT 1 FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE");

            if (!await _context.Seats.AnyAsync(s => s.Id == seatId))
                return BookingResult.Failure(BookingError.SeatNotFound);

            // Lazy expiry: a hold on this seat that ran out must stop counting as active,
            // otherwise the unique index would reject the new hold.
            await _context.Bookings
                .Where(b => b.SeatId == seatId && b.Status == BookingStatus.Held && b.ExpiresAt <= now)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BookingStatus.Expired));

            var active = await _context.Bookings.AsNoTracking().FirstOrDefaultAsync(b =>
                b.SeatId == seatId && (b.Status == BookingStatus.Held || b.Status == BookingStatus.Confirmed));
            if (active != null)
            {
                if (active.Status == BookingStatus.Confirmed) return BookingResult.Failure(BookingError.SeatAlreadyBooked);
                if (active.UserId != userId) return BookingResult.Failure(BookingError.SeatHeld);

                await transaction.CommitAsync();
                return BookingResult.Unchanged(active);
            }

            var activeHolds = await _context.Bookings.CountAsync(b =>
                b.UserId == userId && b.Status == BookingStatus.Held && b.ExpiresAt > now);
            if (activeHolds >= _options.MaxActivePerUser) return BookingResult.Failure(BookingError.HoldLimitReached);

            var booking = new Booking
            {
                SeatId = seatId,
                UserId = userId,
                Status = BookingStatus.Held,
                CreatedAt = now,
                ExpiresAt = now.AddSeconds(_options.DurationSeconds)
            };
            _context.Bookings.Add(booking);

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // The database is the source of truth: its unique index allows one active booking per seat.
                // We get here if someone else took the seat since we checked, for example because the lock failed.
                return BookingResult.Failure(BookingError.SeatHeld);
            }

            await transaction.CommitAsync();

            return BookingResult.Success(booking);
        }

        // Confirms directly for now. The simulated payment will sit between the hold and this step.
        public async Task<BookingResult> ConfirmAsync(int bookingId, int userId)
        {
            var now = UtcNow();

            // One conditional update, so a confirm and an expiry of the same hold cannot both win.
            var updated = await _context.Bookings
                .Where(b => b.Id == bookingId && b.UserId == userId && b.Status == BookingStatus.Held && b.ExpiresAt > now)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, BookingStatus.Confirmed)
                    .SetProperty(b => b.ConfirmedAt, now));

            var booking = await _context.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId);

            if (updated == 0)
            {
                // Nothing changed; find out why.
                if (booking == null) return BookingResult.Failure(BookingError.BookingNotFound);
                if (booking.UserId != userId) return BookingResult.Failure(BookingError.NotHoldOwner);

                return booking.Status switch
                {
                    // Confirming twice is fine for the owner.
                    BookingStatus.Confirmed => BookingResult.Unchanged(booking),
                    BookingStatus.Released => BookingResult.Failure(BookingError.HoldNotActive),
                    // Expired, or still Held but out of time.
                    _ => BookingResult.Failure(BookingError.HoldExpired)
                };
            }

            // Publish event to rabbitmq
            // using an anonymous object that matches the interface
            await _publishEndpoint.Publish<BookingCreatedEvent>(new
            {
                booking!.SeatId,
                booking.UserId,
                CreatedAt = now
            });

            return BookingResult.Success(booking);
        }

        public async Task<BookingResult> ReleaseAsync(int bookingId, int userId)
        {
            var updated = await _context.Bookings
                .Where(b => b.Id == bookingId && b.UserId == userId && b.Status == BookingStatus.Held)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BookingStatus.Released));

            var booking = await _context.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId);

            if (booking == null) return BookingResult.Failure(BookingError.BookingNotFound);
            if (booking.UserId != userId) return BookingResult.Failure(BookingError.NotHoldOwner);
            if (updated == 0) return BookingResult.Failure(BookingError.HoldNotActive);

            return BookingResult.Success(booking);
        }

        // Marks every hold that ran out as Expired and returns how many there were.
        public async Task<int> ExpireDueHoldsAsync(CancellationToken cancellationToken = default)
        {
            var now = UtcNow();

            return await _context.Bookings
                .Where(b => b.Status == BookingStatus.Held && b.ExpiresAt <= now)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BookingStatus.Expired), cancellationToken);
        }
    }
}
