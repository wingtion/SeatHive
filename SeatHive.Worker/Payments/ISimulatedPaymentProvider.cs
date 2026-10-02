namespace SeatHive.Worker.Payments
{
    public record ChargeResult(bool Succeeded, string? Reason);

    // Stands in for an external payment provider. No money moves.
    public interface ISimulatedPaymentProvider
    {
        // Charges once per idempotency key: a repeated call with the same key returns the result of the first one.
        // forceFailure is the demo switch that makes the charge fail for certain.
        Task<ChargeResult> ChargeAsync(Guid idempotencyKey, bool forceFailure, CancellationToken cancellationToken = default);

        // Forgets the charges that are older than the retention period and returns how many there were.
        Task<int> DeleteExpiredChargesAsync(CancellationToken cancellationToken = default);
    }
}
