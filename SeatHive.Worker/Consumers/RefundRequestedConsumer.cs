using MassTransit;
using SeatHive.Shared.Events;

namespace SeatHive.Worker.Consumers
{
    // Simulates giving the money back for a payment whose booking could not be confirmed. No money moves.
    public class RefundRequestedConsumer : IConsumer<RefundRequested>
    {
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<RefundRequestedConsumer> _logger;

        public RefundRequestedConsumer(TimeProvider timeProvider, ILogger<RefundRequestedConsumer> logger)
        {
            _timeProvider = timeProvider;
            _logger = logger;
        }

        public async Task Consume(ConsumeContext<RefundRequested> context)
        {
            var message = context.Message;

            _logger.LogInformation(
                "Simulated refund of payment {PaymentId} for booking {BookingId} ({Reason}). No money was moved.",
                message.PaymentId, message.BookingId, message.Reason);

            await context.Publish(new RefundCompleted(
                message.BookingId, message.SeatId, message.UserId, _timeProvider.GetUtcNow().UtcDateTime, message.PaymentId));
        }
    }
}
