namespace SeatHive.Shared.Events
{
    // The demo data was reset: every event, seat and booking was replaced.
    // It belongs to no booking, so it carries only when it happened.
    public record DemoDataReset(DateTime OccurredAt);
}
