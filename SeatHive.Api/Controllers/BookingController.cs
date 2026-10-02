using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SeatHive.Api.Models;
using SeatHive.Api.Services;

namespace SeatHive.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Booking)]
    public class BookingController : ControllerBase
    {
        private readonly BookingService _bookingService;

        public BookingController(BookingService bookingService)
        {
            _bookingService = bookingService;
        }

        [HttpPost("hold")]
        public async Task<IActionResult> HoldSeat([FromBody] BookingRequest request)
        {
            // The user is always the one in the token, never one sent by the client.
            // (This prevents User 1 from booking as User 999)
            if (!User.TryGetUserId(out var userId)) return InvalidToken();

            var result = await _bookingService.HoldSeatAsync(request.SeatId, userId);
            if (result.Booking == null) return ToProblem(result.Error);

            return Ok(new
            {
                BookingId = result.Booking.Id,
                result.Booking.SeatId,
                Status = result.Booking.Status.ToString(),
                result.Booking.ExpiresAt
            });
        }

        [HttpPost("{id:int}/confirm")]
        public async Task<IActionResult> Confirm(int id)
        {
            if (!User.TryGetUserId(out var userId)) return InvalidToken();

            var result = await _bookingService.ConfirmAsync(id, userId);
            if (result.Booking == null) return ToProblem(result.Error);

            return Ok(new
            {
                BookingId = result.Booking.Id,
                result.Booking.SeatId,
                Status = result.Booking.Status.ToString(),
                result.Booking.ConfirmedAt
            });
        }

        [HttpPost("{id:int}/release")]
        public async Task<IActionResult> Release(int id)
        {
            if (!User.TryGetUserId(out var userId)) return InvalidToken();

            var result = await _bookingService.ReleaseAsync(id, userId);
            if (result.Booking == null) return ToProblem(result.Error);

            return Ok(new
            {
                BookingId = result.Booking.Id,
                result.Booking.SeatId,
                Status = result.Booking.Status.ToString()
            });
        }

        private ObjectResult InvalidToken()
        {
            return this.ProblemWithCode(StatusCodes.Status401Unauthorized, ErrorCodes.InvalidToken, "The token has no valid user id.");
        }

        private ObjectResult ToProblem(BookingError? error)
        {
            return error switch
            {
                BookingError.SeatNotFound =>
                    this.ProblemWithCode(StatusCodes.Status404NotFound, ErrorCodes.SeatNotFound, "Seat not found."),
                BookingError.SeatLocked =>
                    this.ProblemWithCode(StatusCodes.Status409Conflict, ErrorCodes.SeatLocked, "Someone else is booking this seat right now."),
                BookingError.SeatHeld =>
                    this.ProblemWithCode(StatusCodes.Status409Conflict, ErrorCodes.SeatHeld, "Seat is held by another user."),
                BookingError.HoldLimitReached =>
                    this.ProblemWithCode(StatusCodes.Status409Conflict, ErrorCodes.HoldLimitReached, "You are holding the maximum number of seats."),
                BookingError.HoldExpired =>
                    this.ProblemWithCode(StatusCodes.Status410Gone, ErrorCodes.HoldExpired, "The hold has expired."),
                BookingError.HoldNotActive =>
                    this.ProblemWithCode(StatusCodes.Status409Conflict, ErrorCodes.HoldNotActive, "The booking is no longer an active hold."),
                BookingError.BookingNotFound =>
                    this.ProblemWithCode(StatusCodes.Status404NotFound, ErrorCodes.BookingNotFound, "Booking not found."),
                BookingError.NotHoldOwner =>
                    this.ProblemWithCode(StatusCodes.Status403Forbidden, ErrorCodes.NotHoldOwner, "The booking belongs to another user."),
                _ =>
                    this.ProblemWithCode(StatusCodes.Status409Conflict, ErrorCodes.SeatAlreadyBooked, "Seat is already booked.")
            };
        }
    }
}
