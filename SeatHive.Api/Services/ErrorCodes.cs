using Microsoft.AspNetCore.Mvc;

namespace SeatHive.Api.Services
{
    // Machine-readable codes sent in the "code" field of every error body.
    // Clients switch on these, so treat them as part of the API contract.
    public static class ErrorCodes
    {
        public const string EventNotFound = "event_not_found";
        public const string SeatNotFound = "seat_not_found";
        public const string SeatAlreadyBooked = "seat_already_booked";
        public const string SeatLocked = "seat_locked";
        public const string SeatHeld = "seat_held";
        public const string HoldLimitReached = "hold_limit_reached";
        public const string HoldExpired = "hold_expired";
        public const string HoldNotActive = "hold_not_active";
        public const string PaymentInProgress = "payment_in_progress";
        public const string BookingNotFound = "booking_not_found";
        public const string NotHoldOwner = "not_hold_owner";
        public const string ValidationFailed = "validation_failed";
        public const string InvalidToken = "invalid_token";
        public const string EmailAlreadyRegistered = "email_already_registered";
        public const string InvalidCredentials = "invalid_credentials";

        // The race simulation.
        // The winner of an earlier race still holds the seat; the body says when it lets go ("releasesAt").
        public const string SeatHeldByRace = "seat_held_by_race";
        // Another race is running; one runs at a time.
        public const string RaceInProgress = "race_in_progress";
        // Too few racers are free: the others still hold seats they won.
        public const string RacersBusy = "racers_busy";

        // For responses that are produced outside a controller: no or a bad token, the wrong role, a rate limit,
        // an unexpected error.
        public const string Unauthorized = "unauthorized";
        public const string Forbidden = "forbidden";
        public const string RateLimited = "rate_limited";
        public const string InternalError = "internal_error";

        // The code for an error that has no more specific one, by status code. Null when there is none.
        public static string? ForStatus(int? statusCode) => statusCode switch
        {
            StatusCodes.Status401Unauthorized => Unauthorized,
            StatusCodes.Status403Forbidden => Forbidden,
            StatusCodes.Status429TooManyRequests => RateLimited,
            StatusCodes.Status500InternalServerError => InternalError,
            _ => null
        };

        // How a booking error is answered: its status code, its code and a title for people.
        public static (int Status, string Code, string Title) Describe(BookingError? error) => error switch
        {
            BookingError.SeatNotFound => (StatusCodes.Status404NotFound, SeatNotFound, "Seat not found."),
            BookingError.SeatLocked => (StatusCodes.Status409Conflict, SeatLocked, "Someone else is booking this seat right now."),
            BookingError.SeatHeld => (StatusCodes.Status409Conflict, SeatHeld, "Seat is held by another user."),
            BookingError.HoldLimitReached => (StatusCodes.Status409Conflict, HoldLimitReached, "You are holding the maximum number of seats."),
            BookingError.HoldExpired => (StatusCodes.Status410Gone, HoldExpired, "The hold has expired."),
            BookingError.HoldNotActive => (StatusCodes.Status409Conflict, HoldNotActive, "The booking is no longer an active hold."),
            BookingError.PaymentInProgress => (StatusCodes.Status409Conflict, PaymentInProgress, "The payment for this booking is being processed."),
            BookingError.BookingNotFound => (StatusCodes.Status404NotFound, BookingNotFound, "Booking not found."),
            BookingError.NotHoldOwner => (StatusCodes.Status403Forbidden, NotHoldOwner, "The booking belongs to another user."),
            _ => (StatusCodes.Status409Conflict, SeatAlreadyBooked, "Seat is already booked.")
        };
    }

    public static class ProblemExtensions
    {
        // A ProblemDetails response that also carries one of the codes above.
        public static ObjectResult ProblemWithCode(this ControllerBase controller, int statusCode, string code, string title)
        {
            var result = controller.Problem(statusCode: statusCode, title: title);

            // Set, not added: the status code may already have brought its general code (see ForStatus),
            // and the specific one given here replaces it.
            ((ProblemDetails)result.Value!).Extensions["code"] = code;

            return result;
        }
    }
}
