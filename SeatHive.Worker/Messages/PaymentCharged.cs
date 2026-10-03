namespace SeatHive.Worker.Messages
{
    // Internal to the Worker: the provider has answered for one payment attempt.
    // It separates the slow call to the provider, which runs without a database transaction,
    // from recording and announcing the result, which is one short transaction.
    public record PaymentCharged(int BookingId, int SeatId, int UserId, Guid PaymentId, bool Succeeded, string? Reason);
}
