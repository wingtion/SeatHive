namespace SeatHive.Api.Models
{
    // What the read endpoints return. Entities are never serialized directly.

    public record EventResponse(int Id, string Name, DateTime Date, int SeatCount);

    public enum SeatStatus
    {
        Available,
        Held,
        Booked
    }

    // Public: anyone can read it. It says whether a seat is taken and until when, never by whom or by which booking.
    public record SeatResponse(int SeatId, string Section, string Row, int SeatNumber, SeatStatus Status, DateTime? HeldUntil);

    public record BookingSeat(int SeatId, string Section, string Row, int SeatNumber);

    public record BookingEventInfo(int Id, string Name, DateTime Date);

    public record BookingResponse(
        int BookingId,
        BookingStatus Status,
        DateTime CreatedAt,
        DateTime? ExpiresAt,
        DateTime? ConfirmedAt,
        BookingSeat Seat,
        BookingEventInfo Event);
}
