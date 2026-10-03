using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SeatHive.Api.Data;
using SeatHive.Api.Models;

namespace SeatHive.Api.Services
{
    // The one place that says what state a seat is in for the outside world.
    // It follows the same rules as holding a seat: a hold whose time ran out no longer counts,
    // even before it is marked Expired, and a booking waiting for its payment counts until its grace period ends.
    public class SeatStatusQuery
    {
        private readonly AppDbContext _context;
        private readonly TimeProvider _timeProvider;
        private readonly HoldOptions _options;

        public SeatStatusQuery(AppDbContext context, TimeProvider timeProvider, IOptions<HoldOptions> options)
        {
            _context = context;
            _timeProvider = timeProvider;
            _options = options.Value;
        }

        // A seat with its event and, if it is taken right now, the booking that takes it.
        private record SeatRow(int SeatId, int EventId, string Section, string Row, int SeatNumber, BookingStatus? Status, DateTime? ExpiresAt);

        private IQueryable<SeatRow> Rows(IQueryable<Seat> seats)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var paymentCutoff = now.AddSeconds(-_options.PaymentGraceSeconds);

            // The partial unique index allows one active booking per seat, so there is at most one match.
            return
                from seat in seats.AsNoTracking()
                let active = seat.Bookings
                    .Where(b => b.Status == BookingStatus.Confirmed
                        || (b.Status == BookingStatus.Held && b.ExpiresAt > now)
                        || (b.Status == BookingStatus.PaymentPending && b.ExpiresAt > paymentCutoff))
                    .Select(b => new { Status = (BookingStatus?)b.Status, b.ExpiresAt })
                    .FirstOrDefault()
                select new SeatRow(seat.Id, seat.EventId, seat.Section, seat.Row, seat.SeatNumber, active!.Status, active.ExpiresAt);
        }

        private SeatResponse ToResponse(SeatRow row)
        {
            return row.Status switch
            {
                BookingStatus.Confirmed => Seat(row, SeatStatus.Booked, null),
                BookingStatus.Held => Seat(row, SeatStatus.Held, row.ExpiresAt),
                // The sweeper gives the seat up when the grace period after the hold has passed.
                BookingStatus.PaymentPending => Seat(row, SeatStatus.Held, row.ExpiresAt?.AddSeconds(_options.PaymentGraceSeconds)),
                _ => Seat(row, SeatStatus.Available, null)
            };

            static SeatResponse Seat(SeatRow row, SeatStatus status, DateTime? heldUntil) =>
                new(row.SeatId, row.Section, row.Row, row.SeatNumber, status, heldUntil);
        }

        public async Task<PagedResponse<SeatResponse>> ForEventAsync(int eventId, int page, int pageSize, CancellationToken cancellationToken = default)
        {
            var seats = _context.Seats.Where(s => s.EventId == eventId);
            var total = await seats.CountAsync(cancellationToken);

            var rows = await Rows(seats
                    .OrderBy(s => s.Section).ThenBy(s => s.Row).ThenBy(s => s.SeatNumber).ThenBy(s => s.Id)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize))
                .ToListAsync(cancellationToken);

            return new PagedResponse<SeatResponse>(rows.Select(ToResponse).ToList(), page, pageSize, total);
        }

        // The seat as it is now, with the event it belongs to. Null when the seat does not exist (any more).
        public async Task<(int EventId, SeatResponse Seat)?> ForSeatAsync(int seatId, CancellationToken cancellationToken = default)
        {
            var row = await Rows(_context.Seats.Where(s => s.Id == seatId)).FirstOrDefaultAsync(cancellationToken);

            return row == null ? null : (row.EventId, ToResponse(row));
        }
    }
}
