using Microsoft.EntityFrameworkCore;
using SeatHive.Api.Data;
using SeatHive.Api.Models;

namespace SeatHive.Api.Services
{
    // Reads the recorded events of a booking in the order they happened.
    public class BookingHistory
    {
        private readonly AppDbContext _context;

        public BookingHistory(AppDbContext context)
        {
            _context = context;
        }

        // Payments, refunds and notifications are simulated by the Worker; holds and confirmations are real.
        public static bool IsSimulated(BookingEventType type) => type is
            BookingEventType.PaymentRequested or BookingEventType.PaymentSucceeded or BookingEventType.PaymentFailed
            or BookingEventType.RefundRequested or BookingEventType.RefundCompleted or BookingEventType.NotificationSent;

        public async Task<PagedResponse<BookingHistoryItem>> ForBookingAsync(int bookingId, int page, int pageSize, CancellationToken cancellationToken = default)
        {
            var events = _context.BookingEvents.AsNoTracking().Where(e => e.BookingId == bookingId);
            var total = await events.CountAsync(cancellationToken);

            var rows = await events
                // 1. When the event happened, by its own timestamp.
                .OrderBy(e => e.OccurredAt)
                // 2. At the same moment: the order of a booking's life cycle (a request before its result, and so on).
                .ThenBy(e =>
                    e.Type == BookingEventType.SeatHeld ? 0
                    : e.Type == BookingEventType.PaymentRequested ? 1
                    : e.Type == BookingEventType.PaymentSucceeded || e.Type == BookingEventType.PaymentFailed ? 2
                    : e.Type == BookingEventType.BookingConfirmed || e.Type == BookingEventType.RefundRequested ? 3
                    : e.Type == BookingEventType.NotificationSent || e.Type == BookingEventType.RefundCompleted ? 4
                    : 5)
                // 3. Still the same: the order in which they were recorded.
                .ThenBy(e => e.Sequence)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(e => new { e.EventId, e.Sequence, e.Type, e.OccurredAt, e.PaymentId, e.Detail })
                .ToListAsync(cancellationToken);

            var items = rows
                .Select(e => new BookingHistoryItem(e.EventId, e.Sequence, e.Type, e.OccurredAt, e.PaymentId, e.Detail, IsSimulated(e.Type)))
                .ToList();

            return new PagedResponse<BookingHistoryItem>(items, page, pageSize, total);
        }
    }
}
