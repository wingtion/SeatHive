namespace SeatHive.Api.Models
{
    // The events of SeatHive.Shared, by name.
    public enum BookingEventType
    {
        SeatHeld,
        HoldReleased,
        HoldExpired,
        PaymentRequested,
        PaymentSucceeded,
        PaymentFailed,
        BookingConfirmed,
        RefundRequested,
        RefundCompleted,
        RefundFailed,
        NotificationSent
    }

    // One event that really happened to a booking, written down when it arrived over the bus.
    // Rows are only ever added. Nothing here is made up: no row without an event.
    public class BookingEvent
    {
        // The order in which events were recorded. It only decides the order of two events
        // that happened at the same moment; see BookingHistory.
        public long Sequence { get; set; }

        // The id of the message that carried the event. Unique, so an event delivered twice is recorded once.
        public Guid EventId { get; set; }

        // Not a foreign key: an event can arrive for a booking that a reset of the demo data has deleted.
        public int BookingId { get; set; }
        public int SeatId { get; set; }
        public int UserId { get; set; }

        public BookingEventType Type { get; set; }

        // When the event happened, as the event itself says. Not when it was recorded.
        public DateTime OccurredAt { get; set; }

        public Guid? PaymentId { get; set; }

        // The reason of a failed payment or a refund, the channel of a notification.
        public string? Detail { get; set; }
    }

    public record BookingHistoryItem(
        Guid EventId,
        long Sequence,
        BookingEventType Type,
        DateTime OccurredAt,
        Guid? PaymentId,
        string? Detail,
        // True for what the Worker only simulates: payments, refunds and notifications.
        bool Simulated);
}
