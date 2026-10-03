using System.ComponentModel.DataAnnotations;
using SeatHive.Api.Services;
using SeatHive.Shared.Events;

namespace SeatHive.Api.Models
{
    // POST /api/simulation/simulate-concurrency. Both are optional.
    public class RaceRequest
    {
        public const int DefaultRacers = 20;

        // Without a seat the race takes the free seat with the lowest id.
        public int? SeatId { get; set; }

        // At most as many as there are racer accounts.
        [Range(2, RacerAccounts.Count)]
        public int? Racers { get; set; }
    }

    // What the starter gets back and what everyone watching the event is sent: the same thing.
    public record RaceReport(
        Guid RaceId,
        int EventId,
        int SeatId,
        int Racers,
        int Winners,
        DateTime StartedAt,
        long DurationMs,
        RaceWinner? Winner,
        IReadOnlyList<RaceAttempt> Attempts)
    {
        public static RaceReport From(RaceFinished race)
        {
            var winner = race.Winner == null
                ? null
                : race.Winner with { ReleasesAt = DateTime.SpecifyKind(race.Winner.ReleasesAt, DateTimeKind.Utc) };

            return new RaceReport(
                race.RaceId,
                race.EventId,
                race.SeatId,
                race.Attempts.Count,
                race.Attempts.Count(a => a.Outcome == RaceOutcomes.Won),
                DateTime.SpecifyKind(race.StartedAt, DateTimeKind.Utc),
                race.DurationMs,
                winner,
                race.Attempts);
        }
    }

    public static class RaceOutcomes
    {
        public const string Won = "won";
        public const string Rejected = "rejected";
        // The attempt failed for a reason that is not about the seat (for example the database); see the log.
        public const string Error = "error";
    }
}
