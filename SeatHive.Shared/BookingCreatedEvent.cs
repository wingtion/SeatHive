namespace SeatHive.Shared.Events
{
    public interface BookingCreatedEvent
    {
        int SeatId { get; }
        int UserId { get; }
        DateTime CreatedAt { get; }
    }
}