using MassTransit;
using Microsoft.EntityFrameworkCore;
using SeatHive.Shared.Events;
using SeatHive.Worker.Data;
using SeatHive.Worker.Messages;

namespace SeatHive.Worker.Consumers
{
    // Second step of a payment: announce the provider's answer. This endpoint has the inbox and outbox,
    // so the announcement is stored in one short transaction and made once per payment attempt.
    public class PaymentChargedConsumer : IConsumer<PaymentCharged>
    {
        private readonly WorkerDbContext _context;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<PaymentChargedConsumer> _logger;

        public PaymentChargedConsumer(WorkerDbContext context, TimeProvider timeProvider, ILogger<PaymentChargedConsumer> logger)
        {
            _context = context;
            _timeProvider = timeProvider;
            _logger = logger;
        }

        public async Task Consume(ConsumeContext<PaymentCharged> context)
        {
            var message = context.Message;
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            // "Once per payment attempt" does not rest on the inbox alone: the charge records that its result was
            // announced, in the transaction this message is consumed in. One conditional update, so of two outcomes
            // for one charge, at the same moment or as separate messages, only one matches the row and announces.
            var announced = await _context.SimulatedCharges
                .Where(c => c.IdempotencyKey == message.PaymentId && c.AnnouncedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.AnnouncedAt, now), context.CancellationToken);

            if (announced == 0)
            {
                _logger.LogInformation(
                    "The result of payment {PaymentId} for booking {BookingId} was already announced, or its charge is unknown; nothing was announced.",
                    message.PaymentId, message.BookingId);
                return;
            }

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
