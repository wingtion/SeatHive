using System.Diagnostics;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SeatHive.Api.Data;
using SeatHive.Api.Models;
using SeatHive.Shared.Events;

namespace SeatHive.Api.Services
{
    // Why a race did not start.
    public record RaceRefusal(int Status, string Code, string Title, DateTime? ReleasesAt = null);

    // The race simulation: racers, each a user of its own, try to hold one seat at the same moment, through the
    // same BookingService.HoldSeatAsync the hold endpoint uses (lock, transaction, unique index). Exactly one may win.
    public class RaceSimulator
    {
        // One race at a time: a race opens a database connection per racer.
        private readonly SemaphoreSlim _oneRace = new(1, 1);
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeProvider _timeProvider;
        private readonly RaceWinnerReleaser _releaser;
        private readonly SimulationOptions _options;
        private readonly ILogger<RaceSimulator> _logger;

        public RaceSimulator(
            IServiceScopeFactory scopeFactory,
            TimeProvider timeProvider,
            RaceWinnerReleaser releaser,
            IOptions<SimulationOptions> options,
            ILogger<RaceSimulator> logger)
        {
            _scopeFactory = scopeFactory;
            _timeProvider = timeProvider;
            _releaser = releaser;
            _options = options.Value;
            _logger = logger;
        }

        private sealed record Racer(int Number, int UserId);

        // Not cancelled by the caller: once racers are running, the winner's release must be scheduled.
        public async Task<(RaceReport? Report, RaceRefusal? Refusal)> RunAsync(int? seatId, int racerCount)
        {
            if (!await _oneRace.WaitAsync(TimeSpan.Zero))
            {
                return (null, new RaceRefusal(StatusCodes.Status409Conflict, ErrorCodes.RaceInProgress,
                    "Another race is running; one runs at a time."));
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = _timeProvider.GetUtcNow().UtcDateTime;

                var seat = await FindSeatAsync(db, seatId, now);
                if (seat == null)
                {
                    return (null, new RaceRefusal(StatusCodes.Status404NotFound, ErrorCodes.SeatNotFound,
                        seatId == null ? "There is no free seat to race for." : "Seat not found."));
                }

                var refusal = await CheckSeatIsFreeAsync(db, seat.Value.SeatId, now);
                if (refusal != null) return (null, refusal);

                await RacerAccounts.EnsureAsync(db);
                var racers = await PickFreeRacersAsync(db, racerCount, now);
                if (racers.Count < racerCount)
                {
                    return (null, new RaceRefusal(StatusCodes.Status409Conflict, ErrorCodes.RacersBusy,
                        $"Only {racers.Count} racers are free; the others still hold seats they won. Try fewer racers or wait."));
                }

                var finished = await RaceAsync(seat.Value.EventId, seat.Value.SeatId, racers, now);

                // Announced like every change: through the outbox, sent after the commit.
                var publisher = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
                await using (var transaction = await db.Database.BeginTransactionAsync())
                {
                    await publisher.Publish(finished);
                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();
                }

                return (RaceReport.From(finished), null);
            }
            finally
            {
                _oneRace.Release();
            }
        }

        // An active booking: one that takes the seat (the same rule as the hold and the seat status).
        private static IQueryable<Booking> Active(AppDbContext db, DateTime now) =>
            db.Bookings.Where(b => b.Status == BookingStatus.Confirmed
                || b.Status == BookingStatus.PaymentPending
                || (b.Status == BookingStatus.Held && b.ExpiresAt > now));

        private static async Task<(int SeatId, int EventId)?> FindSeatAsync(AppDbContext db, int? seatId, DateTime now)
        {
            var active = Active(db, now);
            var seats = db.Seats.AsNoTracking();
            seats = seatId != null
                ? seats.Where(s => s.Id == seatId)
                : seats.Where(s => !active.Any(b => b.SeatId == s.Id)).OrderBy(s => s.Id);

            var seat = await seats.Select(s => new { s.Id, s.EventId }).FirstOrDefaultAsync();
            return seat == null ? null : (seat.Id, seat.EventId);
        }

        // A race on a taken seat could only report that nobody won. It does not start, and says why.
        private async Task<RaceRefusal?> CheckSeatIsFreeAsync(AppDbContext db, int seatId, DateTime now)
        {
            var taken = await Active(db, now).AsNoTracking()
                .Where(b => b.SeatId == seatId)
                .Select(b => new { b.Id, b.Status, b.ExpiresAt, b.User!.Role })
                .FirstOrDefaultAsync();

            if (taken == null) return null;
            if (taken.Status == BookingStatus.Confirmed)
            {
                var (status, code, title) = ErrorCodes.Describe(BookingError.SeatAlreadyBooked);
                return new RaceRefusal(status, code, title);
            }
            if (taken.Role == Roles.Racer)
            {
                // Known here unless the API restarted since; then the hold simply runs out.
                var releasesAt = _releaser.ReleaseTimeOf(taken.Id) ?? taken.ExpiresAt;
                return new RaceRefusal(StatusCodes.Status409Conflict, ErrorCodes.SeatHeldByRace,
                    "The winner of an earlier race still holds this seat.",
                    releasesAt == null ? null : DateTime.SpecifyKind(releasesAt.Value, DateTimeKind.Utc));
            }

            var (heldStatus, heldCode, heldTitle) = ErrorCodes.Describe(BookingError.SeatHeld);
            return new RaceRefusal(heldStatus, heldCode, heldTitle);
        }

        // Racers that hold nothing, in random order. One still holding seats it won is left out: it could lose for a
        // reason that has nothing to do with this seat (its hold limit).
        private static async Task<List<Racer>> PickFreeRacersAsync(AppDbContext db, int count, DateTime now)
        {
            var active = Active(db, now);
            var racers = await db.Users.AsNoTracking()
                .Where(u => u.Role == Roles.Racer && !active.Any(b => b.UserId == u.Id))
                .OrderBy(_ => EF.Functions.Random())
                .Take(count)
                .Select(u => new { u.Id, u.Email })
                .ToListAsync();

            return racers.Select(r => new Racer(RacerAccounts.NumberOf(r.Email), r.Id)).ToList();
        }

        private async Task<RaceFinished> RaceAsync(int eventId, int seatId, List<Racer> racers, DateTime startedAt)
        {
            // Every racer gets its own scope (its own DbContext and connection), like a request of its own.
            var scopes = racers.Select(_ => _scopeFactory.CreateScope()).ToList();
            try
            {
                var ready = 0;
                var allReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var watch = new Stopwatch();

                var attempts = racers.Select((racer, i) => Task.Run(async () =>
                {
                    var service = scopes[i].ServiceProvider.GetRequiredService<BookingService>();

                    // Everyone waits at the start until all are there, then all go at once.
                    if (Interlocked.Increment(ref ready) == racers.Count) allReady.TrySetResult();
                    await go.Task;

                    var began = watch.ElapsedMilliseconds;
                    try
                    {
                        var result = await service.HoldSeatAsync(seatId, racer.UserId);
                        return (racer, result, began, ended: watch.ElapsedMilliseconds, failed: false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Racer {Racer} failed while holding seat {SeatId}.", racer.Number, seatId);
                        return (racer, result: (BookingResult?)null, began, ended: watch.ElapsedMilliseconds, failed: true);
                    }
                })).ToList();

                await allReady.Task;
                watch.Start();
                go.SetResult();
                var results = await Task.WhenAll(attempts);
                watch.Stop();

                // A racer that got the seat; more than one would be the bug this simulation exists to catch.
                var releasesAt = startedAt.AddSeconds(_options.WinnerHoldSeconds);
                var winners = results.Where(r => r.result is { IsSuccess: true }).ToList();
                foreach (var winner in winners)
                {
                    _releaser.Schedule(winner.result!.Booking!.Id, winner.racer.UserId, releasesAt);
                }
                if (winners.Count > 1)
                {
                    _logger.LogCritical("{Count} racers got seat {SeatId}; at most one may.", winners.Count, seatId);
                }

                var first = winners.FirstOrDefault();
                return new RaceFinished(
                    Guid.NewGuid(),
                    eventId,
                    seatId,
                    startedAt,
                    watch.ElapsedMilliseconds,
                    first.result == null ? null : new RaceWinner(first.racer.Number, first.result.Booking!.Id, releasesAt),
                    results.OrderBy(r => r.racer.Number).Select(r => ToAttempt(r.racer, r.result, r.began, r.ended, r.failed)).ToList(),
                    _timeProvider.GetUtcNow().UtcDateTime);
            }
            finally
            {
                foreach (var scope in scopes) scope.Dispose();
            }
        }

        private static RaceAttempt ToAttempt(Racer racer, BookingResult? result, long began, long ended, bool failed)
        {
            if (failed || result == null)
            {
                return new RaceAttempt(racer.Number, RaceOutcomes.Error, ErrorCodes.InternalError, null, began, ended);
            }

            var lockState = result.Lock switch
            {
                LockOutcome.Acquired => "acquired",
                LockOutcome.Busy => "busy",
                LockOutcome.Unavailable => "unavailable",
                _ => null
            };

            // Racers start with no hold of their own, so a success is always a new hold: the seat was won.
            return result.IsSuccess
                ? new RaceAttempt(racer.Number, RaceOutcomes.Won, null, lockState, began, ended)
                : new RaceAttempt(racer.Number, RaceOutcomes.Rejected, ErrorCodes.Describe(result.Error).Code, lockState, began, ended);
        }
    }
}
