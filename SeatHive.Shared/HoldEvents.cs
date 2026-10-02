namespace SeatHive.Shared.Events
{
    // Every event carries BookingId (the correlation id of one booking's story), SeatId, UserId and when it happened.

    public record SeatHeld(int BookingId, int SeatId, int UserId, DateTime OccurredAt, DateTime ExpiresAt);

    public record HoldReleased(int BookingId, int SeatId, int UserId, DateTime OccurredAt);

    public record HoldExpired(int BookingId, int SeatId, int UserId, DateTime OccurredAt);
}
