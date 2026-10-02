using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using SeatHive.Worker.Consumers;
using SeatHive.Worker.Data;

namespace SeatHive.Worker
{
    // The Worker's bus configuration without the transport, so the tests can run the same setup on an in-memory bus.
    public static class WorkerBusSetup
    {
        // Each consumer brings its own endpoint configuration (see ConsumerDefinitions.cs).
        public static void AddWorkerConsumers(this IBusRegistrationConfigurator configurator)
        {
            configurator.AddConsumer<PaymentRequestedConsumer, PaymentRequestedConsumerDefinition>();
            configurator.AddConsumer<PaymentChargedConsumer, InboxConsumerDefinition<PaymentChargedConsumer>>();
            configurator.AddConsumer<RefundRequestedConsumer, InboxConsumerDefinition<RefundRequestedConsumer>>();
            configurator.AddConsumer<BookingConfirmedConsumer, InboxConsumerDefinition<BookingConfirmedConsumer>>();
        }

        // The inbox and outbox tables the consumers above use, in the Worker's own schema.
        public static void AddWorkerOutbox(this IBusRegistrationConfigurator configurator)
        {
            configurator.AddEntityFrameworkOutbox<WorkerDbContext>(o =>
            {
                // MassTransit caches the inbox table names per process and per entity type, not per DbContext.
                // The tests run the API (default schema) and this context ("worker" schema) in one process,
                // where a shared cache would point this context at the API's tables. So it is not cached here.
                o.LockStatementProvider = new PostgresLockStatementProvider(enableSchemaCaching: false);
                o.IsolationLevel = System.Data.IsolationLevel.ReadCommitted;
            });
        }
    }
}
