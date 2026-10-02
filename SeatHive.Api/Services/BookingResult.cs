using SeatHive.Api.Models;

namespace SeatHive.Api.Services
{
    public enum BookingError
    {
        SeatNotFound,
        SeatAlreadyBooked,
        // Another request is working on this seat right now.
        SeatLocked,
        // Another user holds the seat; it may become free again.
        SeatHeld,
        HoldLimitReached,
        HoldExpired,
        // The booking is no longer a hold (released, or already confirmed when releasing).
        HoldNotActive,
        // The hold cannot be released while its payment is being processed.
        PaymentInProgress,
        BookingNotFound,
        NotHoldOwner
    }

    public class BookingResult
    {
        private BookingResult(Booking? booking, BookingError? error, bool changed)
        {
            Booking = booking;
            Error = error;
            Changed = changed;
        }

        public Booking? Booking { get; }
        public BookingError? Error { get; }
        public bool IsSuccess => Booking != null;

        // False when an idempotent call returned a booking that was already in the requested state.
        public bool Changed { get; }

        public static BookingResult Success(Booking booking) => new(booking, null, true);
        public static BookingResult Unchanged(Booking booking) => new(booking, null, false);
        public static BookingResult Failure(BookingError error) => new(null, error, false);
    }
}
