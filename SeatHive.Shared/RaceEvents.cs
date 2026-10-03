namespace SeatHive.Shared.Events
{
    // A race of the simulation is over: racers tried to hold one seat at the same moment.
    // It is announced to everyone watching the seat's event, like a seat change, through the outbox.
    public record RaceFinished(
        Guid RaceId,
        int EventId,
        int SeatId,
        DateTime StartedAt,
        long DurationMs,
        RaceWinner? Winner,
        IReadOnlyList<RaceAttempt> Attempts,
        DateTime OccurredAt);

    // The racer that got the seat, its booking, and when it gives the seat up again.
    public record RaceWinner(int Racer, int BookingId, DateTime ReleasesAt);

    // One racer's try. Outcome: "won", "rejected" or "error". Code: why it lost (an error code such as "seat_locked").
    // Lock: "acquired", "busy" or "unavailable" (Redis could not be asked). Times are milliseconds since the start.
    public record RaceAttempt(int Racer, string Outcome, string? Code, string? Lock, long StartedAtMs, long FinishedAtMs);
}
