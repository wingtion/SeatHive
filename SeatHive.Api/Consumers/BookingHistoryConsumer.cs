using MassTransit;
using Microsoft.EntityFrameworkCore;
using SeatHive.Api.Data;
using SeatHive.Api.Models;
using SeatHive.Api.Services;
using SeatHive.Shared.Events;

namespace SeatHive.Api.Consumers
{
    // Writes down every event of a booking as it arrives over the bus. That is the only way a row gets into
    // the history: an event that was never sent (a change that was rolled back, for example) leaves no trace.
    public class BookingHistoryConsumer :
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
        private readonly AppDbContext _context;

        public BookingHistoryConsumer(AppDbContext context)
        {
            _context = context;
        }

        public Task Consume(ConsumeContext<SeatHeld> context) => RecordAsync(context, BookingEventData.From(context.Message));
        public Task Consume(ConsumeContext<HoldReleased> context) => RecordAsync(context, BookingEventData.From(context.Message));
        public Task Consume(ConsumeContext<HoldExpired> context) => RecordAsync(context, BookingEventData.From(context.Message));
        public Task Consume(ConsumeContext<PaymentRequested> context) => RecordAsync(context, BookingEventData.From(context.Message));
        public Task Consume(ConsumeContext<PaymentSucceeded> context) => RecordAsync(context, BookingEventData.From(context.Message));
        public Task Consume(ConsumeContext<PaymentFailed> context) => RecordAsync(context, BookingEventData.From(context.Message));
        public Task Consume(ConsumeContext<BookingConfirmed> context) => RecordAsync(context, BookingEventData.From(context.Message));
        public Task Consume(ConsumeContext<RefundRequested> context) => RecordAsync(context, BookingEventData.From(context.Message));
        public Task Consume(ConsumeContext<RefundCompleted> context) => RecordAsync(context, BookingEventData.From(context.Message));
        public Task Consume(ConsumeContext<RefundFailed> context) => RecordAsync(context, BookingEventData.From(context.Message));
        public Task Consume(ConsumeContext<NotificationSent> context) => RecordAsync(context, BookingEventData.From(context.Message));

        private async Task RecordAsync(ConsumeContext context, BookingEventData data)
        {
            var (type, bookingId, seatId, userId, occurredAt, paymentId, detail) = data;
            var eventId = context.MessageId
                ?? throw new InvalidOperationException($"A {type} event arrived without a message id and cannot be recorded.");
            var typeName = type.ToString();
            // Events carry UTC; after the trip over the bus the value may have lost that label.
            var occurredAtUtc = DateTime.SpecifyKind(occurredAt, DateTimeKind.Utc);

            // This runs in the transaction of the inbox, which also records that the message was consumed:
            // both are committed together or not at all.
            // The inbox forgets a message after a while. If the same event arrives again after that,
            // the unique event id keeps it from being recorded twice, without failing the transaction.
            await _context.Database.ExecuteSqlAsync($"""
                INSERT INTO "BookingEvents" ("EventId", "BookingId", "SeatId", "UserId", "Type", "OccurredAt", "PaymentId", "Detail")
                VALUES ({eventId}, {bookingId}, {seatId}, {userId}, {typeName}, {occurredAtUtc}, {paymentId}, {detail})
                ON CONFLICT ("EventId") DO NOTHING
                """, context.CancellationToken);
        }
    }

    // An endpoint of its own with the inbox, like the other consumers of the API, and one message at a time,
    // so events are recorded in the order they arrive.
    public class BookingHistoryConsumerDefinition : ConsumerDefinition<BookingHistoryConsumer>
    {
        public const string QueueName = "booking-history";

        public BookingHistoryConsumerDefinition()
        {
            EndpointName = QueueName;
            ConcurrentMessageLimit = 1;
        }

        protected override void ConfigureConsumer(
            IReceiveEndpointConfigurator endpointConfigurator,
            IConsumerConfigurator<BookingHistoryConsumer> consumerConfigurator,
            IRegistrationContext context)
        {
            endpointConfigurator.UseMessageRetry(r => r.Intervals(100, 500, 1000));
            endpointConfigurator.UseEntityFrameworkOutbox<AppDbContext>(context);
        }
    }
}
