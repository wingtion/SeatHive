using MassTransit;
using SeatHive.Shared.Events;
namespace SeatHive.Worker.Consumers
{
    public class BookingConsumer : IConsumer<BookingCreatedEvent>
    {
        private readonly ILogger<BookingConsumer> _logger;

        public BookingConsumer(ILogger<BookingConsumer> logger)
        {
            _logger = logger;
        }

        public async Task Consume(ConsumeContext<BookingCreatedEvent> context)
        {
            var message = context.Message;

            _logger.LogInformation("Booking event received for Seat {SeatId} by User {UserId}", message.SeatId, message.UserId);

            // Simulate slow processing. Nothing is sent or generated yet.
            await Task.Delay(2000);

            _logger.LogInformation("Booking event processed for Seat {SeatId} by User {UserId}", message.SeatId, message.UserId);
        }
    }
}