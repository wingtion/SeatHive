using MassTransit;
using SeatHive.Api.Data;

namespace SeatHive.Api.Consumers
{
    // How the API's consumers are attached to their endpoint: with retries and with the inbox,
    // so a message that is delivered twice is consumed once and what the consumer publishes is stored
    // in the same transaction. It is part of the consumer's own definition, so it only ever applies to that consumer.
    public class InboxConsumerDefinition<TConsumer> : ConsumerDefinition<TConsumer>
        where TConsumer : class, IConsumer
    {
        protected override void ConfigureConsumer(
            IReceiveEndpointConfigurator endpointConfigurator,
            IConsumerConfigurator<TConsumer> consumerConfigurator,
            IRegistrationContext context)
        {
            endpointConfigurator.UseMessageRetry(r => r.Intervals(100, 500, 1000));
            endpointConfigurator.UseEntityFrameworkOutbox<AppDbContext>(context);
        }
    }
}
