using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SeatHive.Api.Data;
using SeatHive.Api.Models;
using SeatHive.Shared.Events;
using Event = SeatHive.Api.Models.Event;

namespace SeatHive.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Roles = Roles.Admin)]
    public class SetupController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IPublishEndpoint _publishEndpoint;
        private readonly TimeProvider _timeProvider;

        public SetupController(AppDbContext context, IPublishEndpoint publishEndpoint, TimeProvider timeProvider)
        {
            _context = context;
            _publishEndpoint = publishEndpoint;
            _timeProvider = timeProvider;
        }

        [HttpPost("create-data")]
        public async Task<IActionResult> CreateData()
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();

            // 1. Reset the demo data only. Users are kept.
            // Seat and event ids start at 1 again. Booking ids are never reused: a payment result or refund
            // for a deleted booking may still be on its way, and it must not meet a new booking with the same id.
            await _context.Database.ExecuteSqlRawAsync(
                "TRUNCATE TABLE \"Bookings\", \"BookingEvents\", \"Seats\", \"Events\"; " +
                "ALTER TABLE \"Seats\" ALTER COLUMN \"Id\" RESTART; " +
                "ALTER TABLE \"Events\" ALTER COLUMN \"Id\" RESTART;");

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

            // Announced like every other change: stored with it in this transaction and sent after the commit,
            // so whoever shows seats live knows that everything it has is out of date.
            await _publishEndpoint.Publish(new DemoDataReset(_timeProvider.GetUtcNow().UtcDateTime));
            await _context.SaveChangesAsync();

            await transaction.CommitAsync();

            return Ok(new { Message = "Database setup complete!", SeatsCreated = seats.Count });
        }
    }
}
