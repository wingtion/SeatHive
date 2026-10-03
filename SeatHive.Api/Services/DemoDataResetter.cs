using MassTransit;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SeatHive.Api.Data;
using SeatHive.Api.Models;
using SeatHive.Shared.Events;
using Event = SeatHive.Api.Models.Event;

namespace SeatHive.Api.Services
{
    public record DemoResetResult(int SeatsCreated, int GuestsRemoved);

    // Puts the demo data back to its start: one event with 100 free seats and no bookings.
    // The admin's reset and the nightly reset (NightlyDemoReset) both go through here.
    public class DemoDataResetter
    {
        private readonly AppDbContext _context;
        private readonly IPublishEndpoint _publishEndpoint;
        private readonly TimeProvider _timeProvider;

        public DemoDataResetter(AppDbContext context, IPublishEndpoint publishEndpoint, TimeProvider timeProvider)
        {
            _context = context;
            _publishEndpoint = publishEndpoint;
            _timeProvider = timeProvider;
        }

        // The reset needs every table to itself. It takes them one after the other while requests that are under way
        // hold some of them, and a request that then asks for a table the reset already has would be a deadlock, which
        // the database ends by failing one of the two: possibly the request. So the reset waits only briefly for
        // a table, gives everything back when it does not get it, and tries again: a request never fails because of it.
        private const int MaxAttempts = 20;
        private const string LockTimeout = "200ms";
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

        public async Task<DemoResetResult> ResetAsync(CancellationToken cancellationToken = default)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await ResetOnceAsync(cancellationToken);
                }
                catch (Exception ex) when (attempt < MaxAttempts && IsLockConflict(ex))
                {
                    // Nothing was changed yet: the transaction is rolled back with everything it did.
                    _context.ChangeTracker.Clear();
                    await Task.Delay(RetryDelay, cancellationToken);
                }
            }
        }

        // The execution strategy wraps what the database reports.
        private static bool IsLockConflict(Exception ex) =>
            (ex as PostgresException ?? ex.InnerException as PostgresException)
                is { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected };

        private async Task<DemoResetResult> ResetOnceAsync(CancellationToken cancellationToken)
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            // The tables in the order requests read them (seats before bookings), so the reset mostly waits
            // without holding anything yet.
            await _context.Database.ExecuteSqlRawAsync(
                $"SET LOCAL lock_timeout = '{LockTimeout}'; " +
                "LOCK TABLE \"Events\", \"Seats\", \"Bookings\", \"BookingEvents\" IN ACCESS EXCLUSIVE MODE;",
                cancellationToken);

            // 1. Reset the demo data only. Users are kept, except guests whose token ran out (step 4).
            // Seat and event ids start at 1 again. Booking ids are never reused: a payment result or refund
            // for a deleted booking may still be on its way, and it must not meet a new booking with the same id.
            await _context.Database.ExecuteSqlRawAsync(
                "TRUNCATE TABLE \"Bookings\", \"BookingEvents\", \"Seats\", \"Events\"; " +
                "ALTER TABLE \"Seats\" ALTER COLUMN \"Id\" RESTART; " +
                "ALTER TABLE \"Events\" ALTER COLUMN \"Id\" RESTART;",
                cancellationToken);

            // 2. Create Event
            var concert = new Event
            {
                Name = "Evening Performance",
                Date = DateTime.UtcNow.AddDays(30)
            };
            _context.Events.Add(concert);
            await _context.SaveChangesAsync(cancellationToken);

            // 3. Create 100 Seats
            var seats = new List<Seat>();
            for (int i = 1; i <= 50; i++)
            {
                seats.Add(new Seat { Section = "A", Row = "1", SeatNumber = i, EventId = concert.Id });
            }
            for (int i = 1; i <= 50; i++)
            {
                seats.Add(new Seat { Section = "B", Row = "1", SeatNumber = i, EventId = concert.Id });
            }

            _context.Seats.AddRange(seats);

            // 4. Remove the guests nobody can be signed in as any more
            var guestsRemoved = await RemoveOldGuestsAsync(cancellationToken);

            // Announced like every other change: stored with it in this transaction and sent after the commit,
            // so whoever shows seats live knows that everything it has is out of date.
            await _publishEndpoint.Publish(new DemoDataReset(_timeProvider.GetUtcNow().UtcDateTime), cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return new DemoResetResult(seats.Count, guestsRemoved);
        }

        // A guest whose token may still work is kept: its bookings are gone, but it can go on booking.
        // The others have no bookings left (they were just deleted), so nothing refers to them.
        private async Task<int> RemoveOldGuestsAsync(CancellationToken cancellationToken)
        {
            var cutoff = _timeProvider.GetUtcNow().UtcDateTime - GuestAccounts.KeptFor;

            return await _context.Users
                .Where(u => u.Role == Roles.Guest && u.CreatedAt < cutoff)
                .ExecuteDeleteAsync(cancellationToken);
        }
    }
}
