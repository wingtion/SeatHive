using MassTransit;
using Microsoft.AspNetCore.SignalR;
using SeatHive.Api.Hubs;
using SeatHive.Api.Models;
using SeatHive.Shared.Events;

namespace SeatHive.Api.Consumers
{
    // Tells everyone watching an event about a race on one of its seats: the same report the starter got back.
    public class RaceBroadcaster : IConsumer<RaceFinished>
    {
        private readonly IHubContext<SeatHub> _hub;

        public RaceBroadcaster(IHubContext<SeatHub> hub)
        {
            _hub = hub;
        }

        public Task Consume(ConsumeContext<RaceFinished> context)
        {
            return _hub.Clients.Group(SeatHub.GroupOf(context.Message.EventId)).SendAsync(
                SeatHub.RaceFinished,
                RaceReport.From(context.Message),
                context.CancellationToken);
        }
    }

    // No inbox: nothing is written. A report may be sent again after a failure; it carries its race id.
    public class RaceBroadcasterDefinition : ConsumerDefinition<RaceBroadcaster>
    {
        public const string QueueName = "race-broadcast";

        public RaceBroadcasterDefinition()
        {
            EndpointName = QueueName;
        }

        protected override void ConfigureConsumer(
            IReceiveEndpointConfigurator endpointConfigurator,
            IConsumerConfigurator<RaceBroadcaster> consumerConfigurator,
            IRegistrationContext context)
        {
            endpointConfigurator.UseMessageRetry(r => r.Intervals(100, 500, 1000));
        }
    }
}
