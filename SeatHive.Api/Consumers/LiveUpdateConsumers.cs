using MassTransit;
using Microsoft.AspNetCore.SignalR;
using SeatHive.Api.Hubs;
using SeatHive.Api.Services;
using SeatHive.Shared.Events;

namespace SeatHive.Api.Consumers
{
    // Tells everyone watching an event that one of its seats changed.
    //
    // An event from the bus is only the reason to look: what is sent is the state of the seat as it is in the
    // database at that moment (the same answer GET /api/events/{id}/seats gives). So an event that arrives late,
    // out of order or twice cannot announce a state the seat is no longer in.
    public class SeatStatusBroadcaster :
        IConsumer<SeatHeld>,
        IConsumer<HoldReleased>,
        IConsumer<HoldExpired>,
        IConsumer<BookingConfirmed>,
        // The seat stays held through these two, but until when changes.
        IConsumer<PaymentRequested>,
        IConsumer<PaymentFailed>,
        IConsumer<DemoDataReset>
    {
        private readonly SeatStatusQuery _seatStatus;
        private readonly IHubContext<SeatHub> _hub;

        public SeatStatusBroadcaster(SeatStatusQuery seatStatus, IHubContext<SeatHub> hub)
        {
            _seatStatus = seatStatus;
            _hub = hub;
        }

        public Task Consume(ConsumeContext<SeatHeld> context) => AnnounceAsync(context.Message.SeatId, context.CancellationToken);
        public Task Consume(ConsumeContext<HoldReleased> context) => AnnounceAsync(context.Message.SeatId, context.CancellationToken);
        public Task Consume(ConsumeContext<HoldExpired> context) => AnnounceAsync(context.Message.SeatId, context.CancellationToken);
        public Task Consume(ConsumeContext<BookingConfirmed> context) => AnnounceAsync(context.Message.SeatId, context.CancellationToken);
        public Task Consume(ConsumeContext<PaymentRequested> context) => AnnounceAsync(context.Message.SeatId, context.CancellationToken);
        public Task Consume(ConsumeContext<PaymentFailed> context) => AnnounceAsync(context.Message.SeatId, context.CancellationToken);

        // Everything a client has is out of date; it reads it again.
        public Task Consume(ConsumeContext<DemoDataReset> context)
        {
            return _hub.Clients.All.SendAsync(SeatHub.DemoDataReset, context.CancellationToken);
        }

        private async Task AnnounceAsync(int seatId, CancellationToken cancellationToken)
        {
            var current = await _seatStatus.ForSeatAsync(seatId, cancellationToken);
            // The seat is gone: the demo data was reset since the event was sent.
            if (current == null) return;

            var (eventId, seat) = current.Value;
            await _hub.Clients.Group(SeatHub.GroupOf(eventId)).SendAsync(
                SeatHub.SeatStatusChanged,
                new SeatStatusMessage(eventId, seat.SeatId, seat.Status, seat.HeldUntil),
                cancellationToken);
        }
    }

    // One message at a time: each reads the seat and sends what it found, so the last message a client gets
    // about a seat is the newest state. No inbox: nothing is written, and sending a state twice does no harm.
    public class SeatStatusBroadcasterDefinition : ConsumerDefinition<SeatStatusBroadcaster>
    {
        public const string QueueName = "seat-status-broadcast";

        public SeatStatusBroadcasterDefinition()
        {
            EndpointName = QueueName;
            ConcurrentMessageLimit = 1;
        }

        protected override void ConfigureConsumer(
            IReceiveEndpointConfigurator endpointConfigurator,
            IConsumerConfigurator<SeatStatusBroadcaster> consumerConfigurator,
            IRegistrationContext context)
        {
            endpointConfigurator.UseMessageRetry(r => r.Intervals(100, 500, 1000));
        }
    }

    // Tells the owner of a booking, on all their connections, what happened to it: every event of the booking,
    // with the id it also has in the booking's history.
    public class BookingLiveNotifier :
        IConsumer<SeatHeld>,
        IConsumer<HoldReleased>,
        IConsumer<HoldExpired>,
        IConsumer<PaymentRequested>,
        IConsumer<PaymentSucceeded>,
        IConsumer<PaymentFailed>,
        IConsumer<BookingConfirmed>,
        IConsumer<RefundRequested>,
        IConsumer<RefundCompleted>,
        IConsumer<RefundFailed>,
        IConsumer<NotificationSent>
    {
        private readonly IHubContext<SeatHub> _hub;

        public BookingLiveNotifier(IHubContext<SeatHub> hub)
        {
            _hub = hub;
        }

        public Task Consume(ConsumeContext<SeatHeld> context) => NotifyAsync(context, BookingEventData.From(context.Message));
        public Task Consume(ConsumeContext<HoldReleased> context) => NotifyAsync(context, BookingEventData.From(context.Message));
        public Task Consume(ConsumeContext<HoldExpired> context) => NotifyAsync(context, BookingEventData.From(context.Message));
        public Task Consume(ConsumeContext<PaymentRequested> context) => NotifyAsync(context, BookingEventData.From(context.Message));
        public Task Consume(ConsumeContext<PaymentSucceeded> context) => NotifyAsync(context, BookingEventData.From(context.Message));
        public Task Consume(ConsumeContext<PaymentFailed> context) => NotifyAsync(context, BookingEventData.From(context.Message));
        public Task Consume(ConsumeContext<BookingConfirmed> context) => NotifyAsync(context, BookingEventData.From(context.Message));
        public Task Consume(ConsumeContext<RefundRequested> context) => NotifyAsync(context, BookingEventData.From(context.Message));
        public Task Consume(ConsumeContext<RefundCompleted> context) => NotifyAsync(context, BookingEventData.From(context.Message));
        public Task Consume(ConsumeContext<RefundFailed> context) => NotifyAsync(context, BookingEventData.From(context.Message));
        public Task Consume(ConsumeContext<NotificationSent> context) => NotifyAsync(context, BookingEventData.From(context.Message));

        private Task NotifyAsync(ConsumeContext context, BookingEventData data)
        {
            var eventId = context.MessageId
                ?? throw new InvalidOperationException($"A {data.Type} event arrived without a message id and cannot be announced.");

            // Addressed to the user the event itself names: the owner. Nobody else gets it.
            return _hub.Clients.User(data.UserId.ToString()).SendAsync(
                SeatHub.BookingEvent,
                new BookingEventMessage(
                    eventId,
                    data.BookingId,
                    data.SeatId,
                    data.Type,
                    DateTime.SpecifyKind(data.OccurredAt, DateTimeKind.Utc),
                    data.PaymentId,
                    data.Detail,
                    BookingHistory.IsSimulated(data.Type)),
                context.CancellationToken);
        }
    }

    // No inbox: nothing is written. A message may be sent again after a failure; it carries its event id,
    // so a client can tell it has it already.
    public class BookingLiveNotifierDefinition : ConsumerDefinition<BookingLiveNotifier>
    {
        public const string QueueName = "booking-live";

        public BookingLiveNotifierDefinition()
        {
            EndpointName = QueueName;
        }

        protected override void ConfigureConsumer(
            IReceiveEndpointConfigurator endpointConfigurator,
            IConsumerConfigurator<BookingLiveNotifier> consumerConfigurator,
            IRegistrationContext context)
        {
            endpointConfigurator.UseMessageRetry(r => r.Intervals(100, 500, 1000));
        }
    }
}
