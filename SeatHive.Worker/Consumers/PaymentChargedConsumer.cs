using MassTransit;
using SeatHive.Shared.Events;
using SeatHive.Worker.Messages;

namespace SeatHive.Worker.Consumers
{
    // Second step of a payment: announce the provider's answer. This endpoint has the inbox and outbox,
    // so the announcement is stored in one short transaction and made once per payment attempt.
    public class PaymentChargedConsumer : IConsumer<PaymentCharged>
    {
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<PaymentChargedConsumer> _logger;

        public PaymentChargedConsumer(TimeProvider timeProvider, ILogger<PaymentChargedConsumer> logger)
        {
            _timeProvider = timeProvider;
            _logger = logger;
        }

        public async Task Consume(ConsumeContext<PaymentCharged> context)
        {
            var message = context.Message;
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            if (message.Succeeded)
            {
                _logger.LogInformation("Simulated payment {PaymentId} for booking {BookingId} succeeded. No money was moved.", message.PaymentId, message.BookingId);
                await context.Publish(new PaymentSucceeded(message.BookingId, message.SeatId, message.UserId, now, message.PaymentId));
            }
            else
            {
                _logger.LogInformation("Simulated payment {PaymentId} for booking {BookingId} failed ({Reason}).", message.PaymentId, message.BookingId, message.Reason);
                await context.Publish(new PaymentFailed(message.BookingId, message.SeatId, message.UserId, now, message.PaymentId, message.Reason ?? "declined"));
            }
        }
    }
}
