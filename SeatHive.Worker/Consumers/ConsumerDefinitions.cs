using MassTransit;
using SeatHive.Worker.Data;

namespace SeatHive.Worker.Consumers
{
    // How the Worker's consumers are attached to their endpoint: with retries and with the inbox and outbox,
    // so a message that is delivered twice is handled once, and what the consumer publishes is stored in the same
    // transaction as the record of having handled it.
    public class InboxConsumerDefinition<TConsumer> : ConsumerDefinition<TConsumer>
        where TConsumer : class, IConsumer
    {
        protected override void ConfigureConsumer(
            IReceiveEndpointConfigurator endpointConfigurator,
            IConsumerConfigurator<TConsumer> consumerConfigurator,
            IRegistrationContext context)
        {
            endpointConfigurator.UseMessageRetry(r => r.Intervals(100, 500, 1000));
            endpointConfigurator.UseEntityFrameworkOutbox<WorkerDbContext>(context);
        }
    }

    // The one consumer without the inbox: it calls the payment provider, and the inbox would keep a database
    // transaction open for as long as the provider takes. The call is safe to repeat instead,
    // because the provider charges once per payment attempt.
    public class PaymentRequestedConsumerDefinition : ConsumerDefinition<PaymentRequestedConsumer>
    {
        public const string ProviderCallEndpoint = "payment-provider-calls";

        public PaymentRequestedConsumerDefinition()
        {
            EndpointName = ProviderCallEndpoint;
        }

        protected override void ConfigureConsumer(
            IReceiveEndpointConfigurator endpointConfigurator,
            IConsumerConfigurator<PaymentRequestedConsumer> consumerConfigurator,
            IRegistrationContext context)
        {
            endpointConfigurator.UseMessageRetry(r => r.Intervals(100, 500, 1000));
        }
    }
}
