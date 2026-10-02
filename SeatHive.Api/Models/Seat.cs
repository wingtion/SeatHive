namespace SeatHive.Api.Models
{
    public class Seat
    {
        public int Id { get; set; }
        public string Section { get; set; } = string.Empty;
        public string Row { get; set; } = string.Empty;
        public int SeatNumber { get; set; }

        public bool IsBooked { get; set; }
        public int? UserId { get; set; }

        public int EventId { get; set; }
        public Event? Event { get; set; }
    }
}