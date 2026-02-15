using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SeatHive.Api.Models;
using SeatHive.Api.Services;

namespace SeatHive.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
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
            // 1. Get the User ID from the Token (The "Claim")
            var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);

            if (userIdClaim == null)
            {
                return Unauthorized("Invalid Token: No User ID found.");
            }

            // 2. FORCE the Request to use the Token's User ID
            // (This prevents User 1 from booking as User 999)
            int realUserId = int.Parse(userIdClaim.Value);
            request.UserId = realUserId;

            // 3. Call the service
            var result = await _bookingService.BookSeatAsync(request);

            if (result == "Booking successful!")
                return Ok(result);

            return BadRequest(result);
        }
    }
}