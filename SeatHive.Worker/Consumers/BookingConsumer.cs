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

            _logger.LogInformation($"[RabbitMQ] Order received for Seat #{message.SeatId} by User {message.UserId}");

            // Simulate slow processing (e.g., Generating PDF Ticket)
            await Task.Delay(2000);

            _logger.LogInformation($"[RabbitMQ] Email sent to User {message.UserId}. processing complete.");
        }
    }
}