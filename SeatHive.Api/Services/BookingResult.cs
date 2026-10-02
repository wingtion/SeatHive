using SeatHive.Api.Models;

namespace SeatHive.Api.Services
{
    public enum BookingError
    {
        SeatNotFound,
        SeatAlreadyBooked,
        // Another request is working on this seat right now.
        SeatLocked
    }

    public class BookingResult
    {
        private BookingResult(Booking? booking, BookingError? error)
        {
            Booking = booking;
            Error = error;
        }

        public Booking? Booking { get; }
        public BookingError? Error { get; }
        public bool IsSuccess => Booking != null;

        public static BookingResult Success(Booking booking) => new(booking, null);
        public static BookingResult Failure(BookingError error) => new(null, error);
    }
}
