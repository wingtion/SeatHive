using MassTransit;
using SeatHive.Shared.Events;

namespace SeatHive.Worker.Consumers
{
    // Simulates telling the user that the booking is confirmed. No email or message is sent.
    public class BookingConfirmedConsumer : IConsumer<BookingConfirmed>
    {
        public const string Channel = "simulated";

        private readonly TimeProvider _timeProvider;
        private readonly ILogger<BookingConfirmedConsumer> _logger;

        public BookingConfirmedConsumer(TimeProvider timeProvider, ILogger<BookingConfirmedConsumer> logger)
        {
            _timeProvider = timeProvider;
            _logger = logger;
        }

        public async Task Consume(ConsumeContext<BookingConfirmed> context)
        {
            var message = context.Message;

            _logger.LogInformation(
                "Simulated notification for booking {BookingId} (seat {SeatId}, user {UserId}). No email or message was sent.",
                message.BookingId, message.SeatId, message.UserId);

            await context.Publish(new NotificationSent(
                message.BookingId, message.SeatId, message.UserId, _timeProvider.GetUtcNow().UtcDateTime, Channel));
        }
    }
}
