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

    // What happened at the seat lock when holding a seat.
    public enum LockOutcome
    {
        // This request held the lock while it worked.
        Acquired,
        // Another request held it; this one was turned away there (SeatLocked).
        Busy,
        // Redis could not be asked; the request went on without the lock and the database decided.
        Unavailable
    }

    public class BookingResult
    {
        private BookingResult(Booking? booking, BookingError? error, bool changed, LockOutcome? lockOutcome = null)
        {
            Booking = booking;
            Error = error;
            Changed = changed;
            Lock = lockOutcome;
        }

        public Booking? Booking { get; }
        public BookingError? Error { get; }
        public bool IsSuccess => Booking != null;

        // False when an idempotent call returned a booking that was already in the requested state.
        public bool Changed { get; }

        // For a hold: what happened at the lock. Null when the lock was not asked for (an existing hold was returned).
        public LockOutcome? Lock { get; }

        public BookingResult WithLock(LockOutcome lockOutcome) => new(Booking, Error, Changed, lockOutcome);

        public static BookingResult Success(Booking booking) => new(booking, null, true);
        public static BookingResult Unchanged(Booking booking) => new(booking, null, false);
        public static BookingResult Failure(BookingError error) => new(null, error, false);
    }
}
