using Microsoft.AspNetCore.Mvc;
using SeatHive.Api.Data;
using SeatHive.Api.Models;

namespace SeatHive.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SetupController : ControllerBase
    {
        private readonly AppDbContext _context;

        public SetupController(AppDbContext context)
        {
            _context = context;
        }

        [HttpPost("create-data")]
        public IActionResult CreateData()
        {
            // 1. Reset Database (Optional: clears everything first)
            _context.Database.EnsureDeleted();
            _context.Database.EnsureCreated();

            // 2. Create Event
            var concert = new Event
            {
                Name = "Tarkan - Harbiye Open Air",
                Date = DateTime.UtcNow.AddDays(30)
            };
            _context.Events.Add(concert);
            _context.SaveChanges();

            // 3. Create 100 Seats
            var seats = new List<Seat>();
            for (int i = 1; i <= 50; i++)
            {
                seats.Add(new Seat { Section = "A", Row = "1", SeatNumber = i, EventId = concert.Id, Version = 1 });
            }
            for (int i = 1; i <= 50; i++)
            {
                seats.Add(new Seat { Section = "B", Row = "1", SeatNumber = i, EventId = concert.Id, Version = 1 });
            }

            _context.Seats.AddRange(seats);
            _context.SaveChanges();

            return Ok(new { Message = "Database setup complete!", SeatsCreated = seats.Count });
        }
    }
}