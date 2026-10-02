namespace SeatHive.Api.Models
{
    public enum BookingStatus
    {
        Held,
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

        // A seat is taken while it has a Held or Confirmed booking.
        // The database allows at most one of those per seat.
        public BookingStatus Status { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public DateTime? ConfirmedAt { get; set; }
    }
}
