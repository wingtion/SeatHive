using MassTransit;
using SeatHive.Shared.Events;
using SeatHive.Worker.Payments;

namespace SeatHive.Worker.Consumers
{
    // Simulates giving the money back for a payment whose booking could not be confirmed. No money moves.
    public class RefundRequestedConsumer : IConsumer<RefundRequested>
    {
        private readonly ISimulatedPaymentProvider _provider;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<RefundRequestedConsumer> _logger;

        public RefundRequestedConsumer(ISimulatedPaymentProvider provider, TimeProvider timeProvider, ILogger<RefundRequestedConsumer> logger)
        {
            _provider = provider;
            _timeProvider = timeProvider;
            _logger = logger;
        }

        public async Task Consume(ConsumeContext<RefundRequested> context)
        {
            var message = context.Message;

            // The charge itself records that it was given back, in the transaction this message is consumed in.
            // So a charge is refunded once however often it is asked for: the inbox stops the same message,
            // and this stops a second message for the same payment.
            var outcome = await _provider.RefundAsync(message.PaymentId, context.CancellationToken);
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            switch (outcome)
            {
                case RefundOutcome.Refunded:
                    _logger.LogInformation(
                        "Simulated refund of payment {PaymentId} for booking {BookingId} ({Reason}). No money was moved.",
                        message.PaymentId, message.BookingId, message.Reason);
                    await context.Publish(new RefundCompleted(message.BookingId, message.SeatId, message.UserId, now, message.PaymentId));
                    break;

                case RefundOutcome.AlreadyRefunded:
                    _logger.LogInformation(
                        "Payment {PaymentId} for booking {BookingId} was already refunded; nothing was done.",
                        message.PaymentId, message.BookingId);
                    break;

                default:
                    // There is nothing to give back. Saying "refund completed" would announce something that did not happen.
                    var reason = outcome == RefundOutcome.ChargeNotFound ? RefundFailureReasons.ChargeNotFound : RefundFailureReasons.ChargeNotSuccessful;
                    _logger.LogWarning(
                        "Refund of payment {PaymentId} for booking {BookingId} could not be made ({Reason}).",
                        message.PaymentId, message.BookingId, reason);
                    await context.Publish(new RefundFailed(message.BookingId, message.SeatId, message.UserId, now, message.PaymentId, reason));
                    break;
            }
        }
    }

    public static class RefundFailureReasons
    {
        public const string ChargeNotFound = "charge_not_found";
        public const string ChargeNotSuccessful = "charge_not_successful";
    }
}
