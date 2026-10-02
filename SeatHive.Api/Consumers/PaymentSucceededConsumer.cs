using MassTransit;
using SeatHive.Api.Services;
using SeatHive.Shared.Events;

namespace SeatHive.Api.Consumers
{
    public class PaymentSucceededConsumer : IConsumer<PaymentSucceeded>
    {
        private readonly BookingService _bookingService;
        private readonly ILogger<PaymentSucceededConsumer> _logger;

        public PaymentSucceededConsumer(BookingService bookingService, ILogger<PaymentSucceededConsumer> logger)
        {
            _bookingService = bookingService;
            _logger = logger;
        }

        public async Task Consume(ConsumeContext<PaymentSucceeded> context)
        {
            var message = context.Message;

            // Confirms the booking, or asks for a refund when it can no longer be confirmed.
            var result = await _bookingService.CompletePaymentAsync(message);

            if (result.IsSuccess)
            {
                _logger.LogInformation("Booking {BookingId} confirmed after payment {PaymentId}.", message.BookingId, message.PaymentId);
            }
            else
            {
                _logger.LogWarning(
                    "Payment {PaymentId} succeeded but booking {BookingId} could not be confirmed ({Error}).",
                    message.PaymentId, message.BookingId, result.Error);
            }
        }
    }
}
