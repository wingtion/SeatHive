using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using SeatHive.Worker.Data;

namespace SeatHive.Worker.Payments
{
    // Keeps its charges in the SimulatedCharges table, the way a real provider keeps them on its own side.
    // Every database call here is a single statement, so no transaction stays open while the "payment" takes its time.
    public class SimulatedPaymentProvider : ISimulatedPaymentProvider
    {
        private readonly WorkerDbContext _context;
        private readonly PaymentOptions _options;
        private readonly TimeProvider _timeProvider;

        public SimulatedPaymentProvider(WorkerDbContext context, IOptions<PaymentOptions> options, TimeProvider timeProvider)
        {
            _context = context;
            _options = options.Value;
            _timeProvider = timeProvider;
        }

        public async Task<ChargeResult> ChargeAsync(Guid idempotencyKey, bool forceFailure, CancellationToken cancellationToken = default)
        {
            // A key that was charged before gets the same answer again, at once.
            var existing = await FindAsync(idempotencyKey, cancellationToken);
            if (existing != null) return existing;

            // A payment takes a moment.
            var delay = TimeSpan.FromMilliseconds(Random.Shared.Next(_options.MinDelayMs, Math.Max(_options.MinDelayMs, _options.MaxDelayMs) + 1));
            await Task.Delay(delay, _timeProvider, cancellationToken);

            // forceFailure is the demo switch: that payment fails for certain. Otherwise it fails at the configured rate.
            var reason = forceFailure ? "forced"
                : Random.Shared.NextDouble() < _options.FailureRate ? "declined"
                : null;

            _context.SimulatedCharges.Add(new SimulatedCharge
            {
                IdempotencyKey = idempotencyKey,
                Succeeded = reason == null,
                Reason = reason,
                ChargedAt = _timeProvider.GetUtcNow().UtcDateTime
            });

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // The same key was charged by another call in the meantime. That charge is the one that counts.
                _context.ChangeTracker.Clear();
            }

            // Answer with what is stored, so every call for one key gives the same result.
            return (await FindAsync(idempotencyKey, cancellationToken))!;
        }

        public async Task<RefundOutcome> RefundAsync(Guid idempotencyKey, CancellationToken cancellationToken = default)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            // One conditional update: of two refunds of the same charge, whenever they come, only one matches the row.
            var refunded = await _context.SimulatedCharges
                .Where(c => c.IdempotencyKey == idempotencyKey && c.Succeeded && c.RefundedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.RefundedAt, now), cancellationToken);
            if (refunded == 1) return RefundOutcome.Refunded;

            // Nothing changed; find out why.
            var charge = await _context.SimulatedCharges.AsNoTracking()
                .FirstOrDefaultAsync(c => c.IdempotencyKey == idempotencyKey, cancellationToken);

            return charge == null ? RefundOutcome.ChargeNotFound
                : !charge.Succeeded ? RefundOutcome.ChargeNotSuccessful
                : RefundOutcome.AlreadyRefunded;
        }

        public async Task<int> DeleteExpiredChargesAsync(CancellationToken cancellationToken = default)
        {
            var cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-_options.ChargeRetentionDays);

            // The retention counts from the announcement of the result, not from the charge:
            // a charge whose result is still on its way is needed to announce it (AnnouncedAt is null, which never
            // matches), and after the announcement a refund may still be asked for.
            return await _context.SimulatedCharges
                .Where(c => c.AnnouncedAt < cutoff)
                .ExecuteDeleteAsync(cancellationToken);
        }

        private async Task<ChargeResult?> FindAsync(Guid idempotencyKey, CancellationToken cancellationToken)
        {
            var charge = await _context.SimulatedCharges.AsNoTracking()
                .FirstOrDefaultAsync(c => c.IdempotencyKey == idempotencyKey, cancellationToken);

            return charge == null ? null : new ChargeResult(charge.Succeeded, charge.Reason);
        }
    }
}
