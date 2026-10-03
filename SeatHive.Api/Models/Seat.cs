namespace SeatHive.Api.Models
{
    public class Seat
    {
        public int Id { get; set; }
        public string Section { get; set; } = string.Empty;
        public string Row { get; set; } = string.Empty;
        public int SeatNumber { get; set; }

        public int EventId { get; set; }
        public Event? Event { get; set; }

        // Whether the seat is taken is not stored here; it follows from its bookings.
        public List<Booking> Bookings { get; set; } = new();
    }
}
