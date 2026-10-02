using System.Data.Common;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SeatHive.Worker.Data
{
    // Makes the MassTransit inbox read its row from the database every time, instead of from memory.
    //
    // The inbox handles a message in several transactions on one DbContext: the first one adds the inbox row,
    // a later one locks it (SELECT ... FOR UPDATE) and runs the consumer if the row does not say "consumed" yet.
    // EF Core does not refresh an entity it already tracks, so that second read returns the object from the first
    // transaction, not what is in the database now. If the same message was delivered a second time in between and
    // that delivery has already consumed it, this one does not see that and runs the consumer again.
    //
    // Forgetting the inbox rows whenever a transaction ends closes that gap: the next read comes from the database,
    // under the row lock. MassTransit does not keep the object across its transactions, so nothing is lost.
    // (MassTransit 8.5.11; the same code is on its develop branch. Related: MassTransit issue 4474.)
    public class InboxStateDetachInterceptor : DbTransactionInterceptor
    {
        public static readonly InboxStateDetachInterceptor Instance = new();

        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
        {
            Detach(eventData.Context);
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Detach(eventData.Context);
            return Task.CompletedTask;
        }

        public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
        {
            Detach(eventData.Context);
        }

        public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Detach(eventData.Context);
            return Task.CompletedTask;
        }

        private static void Detach(DbContext? context)
        {
            if (context == null) return;

            foreach (var entry in context.ChangeTracker.Entries<InboxState>().ToList())
            {
                entry.State = EntityState.Detached;
            }
        }
    }
}
