namespace SeatHive.Shared.Events
{
    public record BookingConfirmed(int BookingId, int SeatId, int UserId, DateTime OccurredAt, Guid PaymentId);

    // Channel says how the user was notified. Today it is always "simulated": nothing is really sent.
    public record NotificationSent(int BookingId, int SeatId, int UserId, DateTime OccurredAt, string Channel);
}
