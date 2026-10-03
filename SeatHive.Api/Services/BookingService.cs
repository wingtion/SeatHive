using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using SeatHive.Api.Data;
using SeatHive.Api.Models;
using SeatHive.Shared.Events;

namespace SeatHive.Api.Services
{
    // Every change of a booking and the event that announces it are written in one database transaction:
    // the event goes to the outbox table and is sent to RabbitMQ after the commit.
    public class BookingService
    {
        private readonly AppDbContext _context;
        private readonly IRedisLockService _lockService;
        private readonly IPublishEndpoint _publishEndpoint;
        private readonly TimeProvider _timeProvider;
        private readonly HoldOptions _options;
        private readonly ILogger<BookingService> _logger;

        public BookingService(
            AppDbContext context,
            IRedisLockService lockService,
            IPublishEndpoint publishEndpoint,
            TimeProvider timeProvider,
            IOptions<HoldOptions> options,
            ILogger<BookingService> logger)
        {
            _context = context;
            _lockService = lockService;
            _publishEndpoint = publishEndpoint;
            _timeProvider = timeProvider;
            _options = options.Value;
            _logger = logger;
        }

        private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

        // Consumers already run inside the inbox transaction, which commits when the message is done.
        // Everything else gets its own transaction; leaving it without a commit rolls everything back.
        private async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            return _context.Database.CurrentTransaction == null
                ? await _context.Database.BeginTransactionAsync(cancellationToken)
                : null;
        }

        // Stores the published events and, when the transaction is ours, commits it.
        private async Task SaveAndCommitAsync(IDbContextTransaction? transaction, CancellationToken cancellationToken = default)
        {
            await _context.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
        }

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
            IAsyncDisposable? lockHandle;
            LockOutcome lockOutcome;
            try
            {
                lockHandle = await _lockService.AcquireLockAsync(lockKey, TimeSpan.FromSeconds(10));
                if (lockHandle == null) return BookingResult.Failure(BookingError.SeatLocked).WithLock(LockOutcome.Busy);
                lockOutcome = LockOutcome.Acquired;
            }
            catch (LockUnavailableException ex)
            {
                // Without Redis the hold goes on without the lock. It is still safe: the unique index lets one
                // active booking per seat through and the user's row lock keeps the hold limit; concurrent requests
                // for the seat are then turned away by the database (seat_held) instead of by the lock.
                _logger.LogWarning(ex, "Holding seat {SeatId} without the Redis lock; the database decides.", seatId);
                lockHandle = null;
                lockOutcome = LockOutcome.Unavailable;
            }

            // The lock is released only after the hold's transaction has ended.
            await using var releaseLock = lockHandle;
            var result = await HoldUnderLockAsync(seatId, userId, now);
            return result.WithLock(lockOutcome);
        }

        // The part of a hold that runs under the seat lock (or, without Redis, without it), in one transaction.
        private async Task<BookingResult> HoldUnderLockAsync(int seatId, int userId, DateTime now)
        {
            await using var transaction = await BeginTransactionAsync();

            // One hold at a time per user, so concurrent requests for different seats cannot pass the limit together.
            await _context.Database.ExecuteSqlAsync($"SELECT 1 FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE");

            if (!await _context.Seats.AnyAsync(s => s.Id == seatId))
                return BookingResult.Failure(BookingError.SeatNotFound);

            // Lazy expiry: a booking on this seat that ran out must stop counting as active,
            // otherwise the unique index would reject the new hold.
            await ExpireAsync(now, seatId);

            var active = await _context.Bookings.AsNoTracking().FirstOrDefaultAsync(b =>
                b.SeatId == seatId
                && (b.Status == BookingStatus.Held || b.Status == BookingStatus.PaymentPending || b.Status == BookingStatus.Confirmed));
            if (active != null)
            {
                if (active.Status == BookingStatus.Confirmed) return BookingResult.Failure(BookingError.SeatAlreadyBooked);
                if (active.UserId != userId) return BookingResult.Failure(BookingError.SeatHeld);

                return BookingResult.Unchanged(active);
            }

            // A booking waiting for its payment still occupies a seat, so it counts as a hold.
            var activeHolds = await _context.Bookings.CountAsync(b =>
                b.UserId == userId
                && ((b.Status == BookingStatus.Held && b.ExpiresAt > now) || b.Status == BookingStatus.PaymentPending));
            if (activeHolds >= _options.MaxActivePerUser) return BookingResult.Failure(BookingError.HoldLimitReached);

            var expiresAt = now.AddSeconds(_options.DurationSeconds);

            // The database is the source of truth: its unique index allows one active booking per seat. If someone
            // else took the seat since we checked (the lock failed, or Redis is down), the insert does nothing and
            // returns no row. Losing that race is an answer, not an error, so it does not fail a command (which
            // would be logged as a database failure). The conflict target is the partial unique index on SeatId;
            // its condition must stay the same as the filter in AppDbContext.
            var inserted = await _context.Bookings.FromSql($"""
                INSERT INTO "Bookings" ("SeatId", "UserId", "Status", "CreatedAt", "ExpiresAt")
                VALUES ({seatId}, {userId}, 'Held', {now}, {expiresAt})
                ON CONFLICT ("SeatId") WHERE "Status" IN ('Held', 'PaymentPending', 'Confirmed') DO NOTHING
                RETURNING *
                """).AsNoTracking().ToListAsync();

            var booking = inserted.SingleOrDefault();
            if (booking == null) return BookingResult.Failure(BookingError.SeatHeld);

            await _publishEndpoint.Publish(new SeatHeld(booking.Id, seatId, userId, now, expiresAt));
            await SaveAndCommitAsync(transaction);

            return BookingResult.Success(booking);
        }

        // Confirming a hold starts its payment. The booking becomes Confirmed when the payment result arrives.
        public async Task<BookingResult> RequestPaymentAsync(int bookingId, int userId, bool forceFailure = false)
        {
            var now = UtcNow();
            var paymentId = Guid.NewGuid();

            await using var transaction = await BeginTransactionAsync();

            // One conditional update, so a confirm and an expiry of the same hold cannot both win.
            var updated = await _context.Bookings
                .Where(b => b.Id == bookingId && b.UserId == userId && b.Status == BookingStatus.Held && b.ExpiresAt > now)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, BookingStatus.PaymentPending)
                    .SetProperty(b => b.PaymentId, paymentId));

            var booking = await _context.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId);

            if (updated == 0)
            {
                // Nothing changed; find out why.
                if (booking == null) return BookingResult.Failure(BookingError.BookingNotFound);
                if (booking.UserId != userId) return BookingResult.Failure(BookingError.NotHoldOwner);

                return booking.Status switch
                {
                    // Confirming again is fine for the owner; it does not start a second payment.
                    BookingStatus.PaymentPending or BookingStatus.Confirmed => BookingResult.Unchanged(booking),
                    BookingStatus.Released => BookingResult.Failure(BookingError.HoldNotActive),
                    // Expired, or still Held but out of time.
                    _ => BookingResult.Failure(BookingError.HoldExpired)
                };
            }

            await _publishEndpoint.Publish(new PaymentRequested(booking!.Id, booking.SeatId, booking.UserId, now, paymentId, forceFailure));
            await SaveAndCommitAsync(transaction);

            return BookingResult.Success(booking);
        }

        // The payment went through: confirm the booking, or ask for a refund if it cannot be confirmed.
        public async Task<BookingResult> CompletePaymentAsync(PaymentSucceeded payment)
        {
            var bookingId = payment.BookingId;
            var paymentId = payment.PaymentId;
            var now = UtcNow();

            await using var transaction = await BeginTransactionAsync();

            // There is no time condition: a booking waiting for its payment stays PaymentPending
            // until the sweeper gives it up, and until then a payment made in time still counts.
            var updated = await _context.Bookings
                .Where(b => b.Id == bookingId && b.Status == BookingStatus.PaymentPending && b.PaymentId == paymentId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, BookingStatus.Confirmed)
                    .SetProperty(b => b.ConfirmedAt, now));

            var booking = await _context.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId);

            if (updated == 1)
            {
                await _publishEndpoint.Publish(new BookingConfirmed(booking!.Id, booking.SeatId, booking.UserId, now, paymentId));
                await SaveAndCommitAsync(transaction);

                return BookingResult.Success(booking);
            }

            // The same result handled a second time: the booking is already confirmed with this payment.
            if (booking != null && booking.Status == BookingStatus.Confirmed && booking.PaymentId == paymentId)
                return BookingResult.Unchanged(booking);

            // The money was taken and nothing was confirmed for it, so it always goes back.
            // The booking may even be gone: resetting the demo data deletes bookings.
            var error = booking == null ? BookingError.BookingNotFound
                : booking.Status == BookingStatus.Expired ? BookingError.HoldExpired
                : BookingError.HoldNotActive;
            var reason = error switch
            {
                BookingError.BookingNotFound => ErrorCodes.BookingNotFound,
                BookingError.HoldExpired => ErrorCodes.HoldExpired,
                _ => ErrorCodes.HoldNotActive
            };

            // Seat and user come from the payment message, which is right even when the booking is not there.
            await _publishEndpoint.Publish(new RefundRequested(bookingId, payment.SeatId, payment.UserId, now, paymentId, reason));
            await SaveAndCommitAsync(transaction);

            return BookingResult.Failure(error);
        }

        // The payment failed: the booking is a hold again and keeps its original deadline,
        // so its owner can try again while the hold lasts.
        public async Task<BookingResult> FailPaymentAsync(int bookingId, Guid paymentId)
        {
            var updated = await _context.Bookings
                .Where(b => b.Id == bookingId && b.Status == BookingStatus.PaymentPending && b.PaymentId == paymentId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BookingStatus.Held));

            var booking = await _context.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId);
            if (booking == null) return BookingResult.Failure(BookingError.BookingNotFound);

            // Zero rows means the result is for an earlier attempt or arrived twice; there is nothing to undo.
            return updated == 1 ? BookingResult.Success(booking) : BookingResult.Unchanged(booking);
        }

        public async Task<BookingResult> ReleaseAsync(int bookingId, int userId)
        {
            await using var transaction = await BeginTransactionAsync();

            var updated = await _context.Bookings
                .Where(b => b.Id == bookingId && b.UserId == userId && b.Status == BookingStatus.Held)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BookingStatus.Released));

            var booking = await _context.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId);

            if (booking == null) return BookingResult.Failure(BookingError.BookingNotFound);
            if (booking.UserId != userId) return BookingResult.Failure(BookingError.NotHoldOwner);
            if (updated == 0)
            {
                return BookingResult.Failure(booking.Status == BookingStatus.PaymentPending
                    ? BookingError.PaymentInProgress
                    : BookingError.HoldNotActive);
            }

            await _publishEndpoint.Publish(new HoldReleased(booking.Id, booking.SeatId, booking.UserId, UtcNow()));
            await SaveAndCommitAsync(transaction);

            return BookingResult.Success(booking);
        }

        // Marks every booking that ran out as Expired and returns how many there were.
        public async Task<int> ExpireDueHoldsAsync(CancellationToken cancellationToken = default)
        {
            await using var transaction = await BeginTransactionAsync(cancellationToken);

            var expired = await ExpireAsync(UtcNow(), seatId: null, cancellationToken);
            await SaveAndCommitAsync(transaction, cancellationToken);

            return expired;
        }

        // Expires what ran out, for one seat or for all, and announces each one.
        // A hold runs out at ExpiresAt. A booking waiting for its payment result gets the grace period on top.
        private async Task<int> ExpireAsync(DateTime now, int? seatId, CancellationToken cancellationToken = default)
        {
            var paymentCutoff = now.AddSeconds(-_options.PaymentGraceSeconds);

            // UPDATE ... RETURNING changes and reads the rows in one statement,
            // so each booking is expired, and announced, by exactly one caller.
            var query = seatId == null
                ? _context.Bookings.FromSql($"""
                    UPDATE "Bookings" SET "Status" = 'Expired'
                    WHERE ("Status" = 'Held' AND "ExpiresAt" <= {now})
                       OR ("Status" = 'PaymentPending' AND "ExpiresAt" <= {paymentCutoff})
                    RETURNING *
                    """)
                : _context.Bookings.FromSql($"""
                    UPDATE "Bookings" SET "Status" = 'Expired'
                    WHERE "SeatId" = {seatId.Value}
                      AND (("Status" = 'Held' AND "ExpiresAt" <= {now})
                        OR ("Status" = 'PaymentPending' AND "ExpiresAt" <= {paymentCutoff}))
                    RETURNING *
                    """);

            var expired = await query.AsNoTracking().ToListAsync(cancellationToken);

            foreach (var booking in expired)
            {
                await _publishEndpoint.Publish(new HoldExpired(booking.Id, booking.SeatId, booking.UserId, now), cancellationToken);
            }

            return expired.Count;
        }
    }
}
