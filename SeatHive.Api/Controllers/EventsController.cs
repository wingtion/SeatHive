using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SeatHive.Api.Data;
using SeatHive.Api.Models;
using SeatHive.Api.Services;

namespace SeatHive.Api.Controllers
{
    // Public, read-only: what is on and which seats are free. No token is needed.
    [ApiController]
    [Route("api/[controller]")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Read)]
    public class EventsController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly SeatStatusQuery _seatStatus;

        public EventsController(AppDbContext context, SeatStatusQuery seatStatus)
        {
            _context = context;
            _seatStatus = seatStatus;
        }

        [HttpGet]
        public async Task<ActionResult<PagedResponse<EventResponse>>> List([FromQuery] PageQuery query, CancellationToken cancellationToken)
        {
            var events = _context.Events.AsNoTracking();
            var total = await events.CountAsync(cancellationToken);

            var items = await events
                .OrderBy(e => e.Date).ThenBy(e => e.Id)
                .Skip((query.Page - 1) * query.PageSize)
                .Take(query.PageSize)
                .Select(e => new EventResponse(e.Id, e.Name, e.Date, e.Seats.Count))
                .ToListAsync(cancellationToken);

            return new PagedResponse<EventResponse>(items, query.Page, query.PageSize, total);
        }

        [HttpGet("{id:int}")]
        public async Task<ActionResult<EventResponse>> Get(int id, CancellationToken cancellationToken)
        {
            var found = await _context.Events.AsNoTracking()
                .Where(e => e.Id == id)
                .Select(e => new EventResponse(e.Id, e.Name, e.Date, e.Seats.Count))
                .FirstOrDefaultAsync(cancellationToken);

            return found == null ? EventNotFound() : found;
        }

        [HttpGet("{id:int}/seats")]
        public async Task<ActionResult<PagedResponse<SeatResponse>>> Seats(int id, [FromQuery] SeatPageQuery query, CancellationToken cancellationToken)
        {
            if (!await _context.Events.AnyAsync(e => e.Id == id, cancellationToken)) return EventNotFound();

            return await _seatStatus.ForEventAsync(id, query.Page, query.PageSize, cancellationToken);
        }

        private ObjectResult EventNotFound()
        {
            return this.ProblemWithCode(StatusCodes.Status404NotFound, ErrorCodes.EventNotFound, "Event not found.");
        }
    }
}
