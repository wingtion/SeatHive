using MassTransit;
using SeatHive.Api.Services;
using SeatHive.Shared.Events;

namespace SeatHive.Api.Consumers
{
    public class PaymentFailedConsumer : IConsumer<PaymentFailed>
    {
        private readonly BookingService _bookingService;
        private readonly ILogger<PaymentFailedConsumer> _logger;

        public PaymentFailedConsumer(BookingService bookingService, ILogger<PaymentFailedConsumer> logger)
        {
            _bookingService = bookingService;
            _logger = logger;
        }

        public async Task Consume(ConsumeContext<PaymentFailed> context)
        {
            var message = context.Message;

            // The booking goes back to being a hold, so its owner can try again while the hold lasts.
            await _bookingService.FailPaymentAsync(message.BookingId, message.PaymentId);

            _logger.LogInformation(
                "Payment {PaymentId} for booking {BookingId} failed ({Reason}).",
                message.PaymentId, message.BookingId, message.Reason);
        }
    }
}
