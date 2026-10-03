using MassTransit;
using SeatHive.Shared.Events;
using SeatHive.Worker.Messages;
using SeatHive.Worker.Payments;

namespace SeatHive.Worker.Consumers
{
    // First step of a payment: ask the provider. Its endpoint has no inbox or outbox,
    // so no database transaction is open while the provider takes its time.
    public class PaymentRequestedConsumer : IConsumer<PaymentRequested>
    {
        private readonly ISimulatedPaymentProvider _provider;

        public PaymentRequestedConsumer(ISimulatedPaymentProvider provider)
        {
            _provider = provider;
        }

        public async Task Consume(ConsumeContext<PaymentRequested> context)
        {
            var message = context.Message;

            // The payment attempt is the idempotency key: if this request is delivered again,
            // the provider returns the first result instead of charging a second time.
            var result = await _provider.ChargeAsync(message.PaymentId, message.ForceFailure, context.CancellationToken);

            // The message id is fixed per attempt too, so the next step's inbox handles a repeated outcome once.
            // If the Worker stops before this is sent, the request comes back and ends up here again.
            await context.Publish(
                new PaymentCharged(message.BookingId, message.SeatId, message.UserId, message.PaymentId, result.Succeeded, result.Reason),
                publish => publish.MessageId = message.PaymentId);
        }
    }
}
