using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SeatHive.Api.Data;
using SeatHive.Api.Models;
using SeatHive.Api.Services;

namespace SeatHive.Api.Hubs
{
    // Live updates. Clients only listen here; nothing a client sends changes a booking.
    //
    // What is sent, and to whom:
    //   seatStatusChanged  to everyone watching an event (JoinEvent): a seat of that event changed.
    //   bookingEvent       to the owner of a booking, on all their connections: something happened to it.
    //   demoDataReset      to everyone: all events, seats and bookings were replaced; read them again.
    //
    // None of it is sent from the request that made the change. Every message starts as an event in the outbox
    // and is sent when that event arrives over the bus (see the consumers), so a change that was rolled back
    // is never announced.
    [Authorize]
    public class SeatHub : Hub
    {
        public const string Path = "/hubs/seats";

        public const string SeatStatusChanged = "seatStatusChanged";
        public const string BookingEvent = "bookingEvent";
        public const string DemoDataReset = "demoDataReset";

        private readonly AppDbContext _context;

        public SeatHub(AppDbContext context)
        {
            _context = context;
        }

        public static string GroupOf(int eventId) => $"event:{eventId}";

        // Start watching the seats of an event. This is the only way into a group: the group's name is built here,
        // from an event that exists, for a connection that has signed in.
        public async Task JoinEvent(int eventId)
        {
            if (!await _context.Events.AnyAsync(e => e.Id == eventId, Context.ConnectionAborted))
            {
                throw new HubException(ErrorCodes.EventNotFound);
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, GroupOf(eventId), Context.ConnectionAborted);
        }

        public Task LeaveEvent(int eventId)
        {
            return Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupOf(eventId), Context.ConnectionAborted);
        }
    }

    // "The user" of a connection is the user id in the token ("sub"), which is what bookingEvent is addressed to.
    public class SubUserIdProvider : IUserIdProvider
    {
        public string? GetUserId(HubConnectionContext connection)
        {
            return connection.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        }
    }

    // Public: it says whether a seat is taken and until when, never by whom or by which booking.
    public record SeatStatusMessage(int EventId, int SeatId, SeatStatus Status, DateTime? HeldUntil);

    // For the owner only. The same fields as an item of the booking's history, with the same event id,
    // so a client can merge the two and drop what it already has.
    public record BookingEventMessage(
        Guid EventId,
        int BookingId,
        int SeatId,
        BookingEventType Type,
        DateTime OccurredAt,
        Guid? PaymentId,
        string? Detail,
        bool Simulated);
}
