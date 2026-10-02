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
            // Bookings belong to real users, so every attempt is made as the admin who started the simulation.
            if (!User.TryGetUserId(out var userId))
            {
                return this.ProblemWithCode(StatusCodes.Status401Unauthorized, ErrorCodes.InvalidToken, "The token has no valid user id.");
            }

            // We will simulate 20 requests trying to hold Seat #1 at the same time.
            var tasks = new List<Task<BookingResult>>();

            // Create 20 concurrent threads
            for (int i = 1; i <= 20; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    // Create a new scope for each "user" (mimics a fresh HTTP request)
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var service = scope.ServiceProvider.GetRequiredService<BookingService>(); // Resolve BookingService

                        // Try to hold Seat #1
                        return await service.HoldSeatAsync(1, userId);
                    }
                }));
            }

            // Wait for all 20 users to finish
            var results = await Task.WhenAll(tasks);

            // Count how many attempts actually took the seat.
            // Every attempt is the same user, so an attempt that only got the existing hold back does not count.
            var successCount = results.Count(r => r.IsSuccess && r.Changed);
            var failCount = results.Length - successCount;

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