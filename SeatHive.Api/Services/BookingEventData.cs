using SeatHive.Api.Models;
using SeatHive.Shared.Events;

namespace SeatHive.Api.Services
{
    // What every booking event has in common, taken from the event itself. The history and the live updates
    // both read events through this, so they always say the same thing about one event.
    public record BookingEventData(
        BookingEventType Type,
        int BookingId,
        int SeatId,
        int UserId,
        DateTime OccurredAt,
        Guid? PaymentId = null,
        // The reason of a failed payment or a refund, the channel of a notification.
        string? Detail = null)
    {
        public static BookingEventData From(SeatHeld m) => new(BookingEventType.SeatHeld, m.BookingId, m.SeatId, m.UserId, m.OccurredAt);
        public static BookingEventData From(HoldReleased m) => new(BookingEventType.HoldReleased, m.BookingId, m.SeatId, m.UserId, m.OccurredAt);
        public static BookingEventData From(HoldExpired m) => new(BookingEventType.HoldExpired, m.BookingId, m.SeatId, m.UserId, m.OccurredAt);
        public static BookingEventData From(PaymentRequested m) => new(BookingEventType.PaymentRequested, m.BookingId, m.SeatId, m.UserId, m.OccurredAt, m.PaymentId);
        public static BookingEventData From(PaymentSucceeded m) => new(BookingEventType.PaymentSucceeded, m.BookingId, m.SeatId, m.UserId, m.OccurredAt, m.PaymentId);
        public static BookingEventData From(PaymentFailed m) => new(BookingEventType.PaymentFailed, m.BookingId, m.SeatId, m.UserId, m.OccurredAt, m.PaymentId, m.Reason);
        public static BookingEventData From(BookingConfirmed m) => new(BookingEventType.BookingConfirmed, m.BookingId, m.SeatId, m.UserId, m.OccurredAt, m.PaymentId);
        public static BookingEventData From(RefundRequested m) => new(BookingEventType.RefundRequested, m.BookingId, m.SeatId, m.UserId, m.OccurredAt, m.PaymentId, m.Reason);
        public static BookingEventData From(RefundCompleted m) => new(BookingEventType.RefundCompleted, m.BookingId, m.SeatId, m.UserId, m.OccurredAt, m.PaymentId);
        public static BookingEventData From(RefundFailed m) => new(BookingEventType.RefundFailed, m.BookingId, m.SeatId, m.UserId, m.OccurredAt, m.PaymentId, m.Reason);
        public static BookingEventData From(NotificationSent m) => new(BookingEventType.NotificationSent, m.BookingId, m.SeatId, m.UserId, m.OccurredAt, Detail: m.Channel);
    }
}
