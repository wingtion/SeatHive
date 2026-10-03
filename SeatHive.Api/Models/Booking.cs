namespace SeatHive.Api.Models
{
    public enum BookingStatus
    {
        Held,
        // The owner confirmed and the payment result has not arrived yet.
        PaymentPending,
        Confirmed,
        Expired,
        Released
    }

    public class Booking
    {
        public int Id { get; set; }

        public int SeatId { get; set; }
        public Seat? Seat { get; set; }

        public int UserId { get; set; }
        public User? User { get; set; }

        // A seat is taken while it has a Held, PaymentPending or Confirmed booking.
        // The database allows at most one of those per seat.
        public BookingStatus Status { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public DateTime? ConfirmedAt { get; set; }

        // The current (or last) payment attempt. Every confirm starts a new one.
        public Guid? PaymentId { get; set; }
    }
}
