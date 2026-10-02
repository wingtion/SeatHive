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
                return Unauthorized("Invalid Token: No User ID found.");
            }

            // 2. Call the service
            var result = await _bookingService.BookSeatAsync(request.SeatId, userId);

            if (result == "Booking successful!")
                return Ok(result);

            return BadRequest(result);
        }
    }
}
