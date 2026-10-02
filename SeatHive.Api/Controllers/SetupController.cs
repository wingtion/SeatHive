using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SeatHive.Api.Data;
using SeatHive.Api.Models;

namespace SeatHive.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Roles = Roles.Admin)]
    public class SetupController : ControllerBase
    {
        private readonly AppDbContext _context;

        public SetupController(AppDbContext context)
        {
            _context = context;
        }

        [HttpPost("create-data")]
        public async Task<IActionResult> CreateData()
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();

            // 1. Reset the demo data only. Users are kept.
            // RESTART IDENTITY makes seat ids start at 1 again.
            await _context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"Seats\", \"Events\" RESTART IDENTITY");

            // 2. Create Event
            var concert = new Event
            {
                Name = "Tarkan - Harbiye Open Air",
                Date = DateTime.UtcNow.AddDays(30)
            };
            _context.Events.Add(concert);
            await _context.SaveChangesAsync();

            // 3. Create 100 Seats
            var seats = new List<Seat>();
            for (int i = 1; i <= 50; i++)
            {
                seats.Add(new Seat { Section = "A", Row = "1", SeatNumber = i, EventId = concert.Id });
            }
            for (int i = 1; i <= 50; i++)
            {
                seats.Add(new Seat { Section = "B", Row = "1", SeatNumber = i, EventId = concert.Id });
            }

            _context.Seats.AddRange(seats);
            await _context.SaveChangesAsync();

            await transaction.CommitAsync();

            return Ok(new { Message = "Database setup complete!", SeatsCreated = seats.Count });
        }
    }
}
