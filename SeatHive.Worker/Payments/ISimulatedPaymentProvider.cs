namespace SeatHive.Worker.Payments
{
    public record ChargeResult(bool Succeeded, string? Reason);

    public enum RefundOutcome
    {
        Refunded,
        // The charge was given back before; nothing happened this time.
        AlreadyRefunded,
        // There is nothing to give back: the provider knows no such charge (any more)...
        ChargeNotFound,
        // ...or the charge had failed, so no money was taken.
        ChargeNotSuccessful
    }

    // Stands in for an external payment provider. No money moves.
    public interface ISimulatedPaymentProvider
    {
        // Charges once per idempotency key: a repeated call with the same key returns the result of the first one.
        // forceFailure is the demo switch that makes the charge fail for certain.
        Task<ChargeResult> ChargeAsync(Guid idempotencyKey, bool forceFailure, CancellationToken cancellationToken = default);

        // Gives a charge back, once: of several calls for one key, at the same moment or later, only one refunds.
        Task<RefundOutcome> RefundAsync(Guid idempotencyKey, CancellationToken cancellationToken = default);

        // Forgets the charges whose result was announced longer ago than the retention period and returns how many
        // there were. A charge whose result has not been announced yet is never forgotten.
        Task<int> DeleteExpiredChargesAsync(CancellationToken cancellationToken = default);
    }
}
