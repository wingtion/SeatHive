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

        [HttpPost]
        public async Task<IActionResult> BookSeat([FromBody] BookingRequest request)
        {
            // 1. The user is always the one in the token, never one sent by the client.
            // (This prevents User 1 from booking as User 999)
            if (!User.TryGetUserId(out var userId))
            {
                return this.ProblemWithCode(StatusCodes.Status401Unauthorized, ErrorCodes.InvalidToken, "The token has no valid user id.");
            }

            // 2. Call the service
            var result = await _bookingService.BookSeatAsync(request.SeatId, userId);

            if (result.Booking != null)
            {
                return Ok(new
                {
                    BookingId = result.Booking.Id,
                    result.Booking.SeatId,
                    Status = result.Booking.Status.ToString()
                });
            }

            return result.Error switch
            {
                BookingError.SeatNotFound =>
                    this.ProblemWithCode(StatusCodes.Status404NotFound, ErrorCodes.SeatNotFound, "Seat not found."),
                BookingError.SeatLocked =>
                    this.ProblemWithCode(StatusCodes.Status409Conflict, ErrorCodes.SeatLocked, "Someone else is booking this seat right now."),
                _ =>
                    this.ProblemWithCode(StatusCodes.Status409Conflict, ErrorCodes.SeatAlreadyBooked, "Seat is already booked.")
            };
        }
    }
}
