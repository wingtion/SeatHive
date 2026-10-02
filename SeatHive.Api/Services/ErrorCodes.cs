using Microsoft.AspNetCore.Mvc;

namespace SeatHive.Api.Services
{
    // Machine-readable codes sent in the "code" field of every error body.
    // Clients switch on these, so treat them as part of the API contract.
    public static class ErrorCodes
    {
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
    }

    public static class ProblemExtensions
    {
        // A ProblemDetails response that also carries one of the codes above.
        public static ObjectResult ProblemWithCode(this ControllerBase controller, int statusCode, string code, string title)
        {
            return controller.Problem(
                statusCode: statusCode,
                title: title,
                extensions: new Dictionary<string, object?> { ["code"] = code });
        }
    }
}
