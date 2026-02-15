namespace SeatHive.Api.Models
{
    public class Event
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty; 
        public DateTime Date { get; set; }

        // Navigation Property
        public List<Seat> Seats { get; set; } = new();
    }
}