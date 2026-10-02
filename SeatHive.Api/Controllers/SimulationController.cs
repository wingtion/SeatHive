using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SeatHive.Api.Services; // Ensure this namespace matches yours
using SeatHive.Api.Models;

namespace SeatHive.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Roles = Roles.Admin)]
    public class SimulationController : ControllerBase
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public SimulationController(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        [HttpPost("simulate-concurrency")]
        public async Task<IActionResult> SimulateConcurrency()
        {
            // We will simulate 20 users trying to book Seat #1 at the same time.
            var tasks = new List<Task<string>>();

            // Create 20 concurrent threads
            for (int i = 1; i <= 20; i++)
            {
                var userId = i + 1000; // User 1001, 1002, etc.

                tasks.Add(Task.Run(async () =>
                {
                    // Create a new scope for each "user" (mimics a fresh HTTP request)
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var service = scope.ServiceProvider.GetRequiredService<BookingService>(); // Resolve BookingService

                        // Try to book Seat #1
                        return await service.BookSeatAsync(1, userId);
                    }
                }));
            }

            // Wait for all 20 users to finish
            var results = await Task.WhenAll(tasks);

            // Count how many people successfully booked the seat
            var successCount = results.Count(r => r == "Booking successful!");
            var failCount = results.Count(r => r != "Booking successful!");

            return Ok(new
            {
                TotalRequests = 20,
                SuccessfulBookings = successCount, // SHOULD BE 1. If > 1, we have a bug!
                FailedBookings = failCount,
                Message = successCount > 1
                    ? "CRITICAL FAIL: Multiple users booked the same seat!"
                    : "System is safe."
            });
        }
    }
}