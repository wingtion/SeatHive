namespace SeatHive.Shared.Events
{
    // PaymentId identifies one payment attempt. A booking gets a new one every time its owner confirms,
    // so the result of an earlier attempt can never be taken for the current one.

    // ForceFailure makes the simulated payment fail for certain, for demos.
    public record PaymentRequested(int BookingId, int SeatId, int UserId, DateTime OccurredAt, Guid PaymentId, bool ForceFailure);

    public record PaymentSucceeded(int BookingId, int SeatId, int UserId, DateTime OccurredAt, Guid PaymentId);

    public record PaymentFailed(int BookingId, int SeatId, int UserId, DateTime OccurredAt, Guid PaymentId, string Reason);

    // The payment went through but the booking could not be confirmed any more.
    public record RefundRequested(int BookingId, int SeatId, int UserId, DateTime OccurredAt, Guid PaymentId, string Reason);

    public record RefundCompleted(int BookingId, int SeatId, int UserId, DateTime OccurredAt, Guid PaymentId);
}
