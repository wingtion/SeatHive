using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using SeatHive.Api.Data;
using SeatHive.Shared.Events;

namespace SeatHive.Tests.Integration.Api
{
    // Live updates over the SignalR hub. Everything a client receives started as an event in the outbox:
    // the API and the Worker's consumers run together here, and nothing is pushed to the hub by hand.
    [Collection(TestCollections.Live)]
    [Trait(TestCategories.Trait, TestCategories.EndToEnd)]
    public class SeatHubTests : IClassFixture<LiveHost>
    {
        private readonly LiveHost _host;
        private readonly ContainersFixture _fixture;
        private readonly ApiFactory _api;
        private readonly string _database;

        public SeatHubTests(LiveHost host)
        {
            _host = host;
            _fixture = host.Fixture;
            _api = host.Api;
            _database = host.Database;
        }

        // One connection to the hub and everything it was sent.
        private sealed class Listener : IAsyncDisposable
        {
            private readonly HubConnection _connection;

            public Listener(HubConnection connection)
            {
                _connection = connection;
                connection.On<JsonElement>("seatStatusChanged", SeatChanges.Enqueue);
                connection.On<JsonElement>("bookingEvent", BookingEvents.Enqueue);
                connection.On("demoDataReset", () => Interlocked.Increment(ref _resets));
            }

            private int _resets;

            public ConcurrentQueue<JsonElement> SeatChanges { get; } = new();
            public ConcurrentQueue<JsonElement> BookingEvents { get; } = new();
            public int Resets => _resets;

            public Task StartAsync() => _connection.StartAsync();
            public Task JoinAsync(int eventId) => _connection.InvokeAsync("JoinEvent", eventId);
            public Task LeaveAsync(int eventId) => _connection.InvokeAsync("LeaveEvent", eventId);

            public List<JsonElement> ChangesOf(int seatId) =>
                SeatChanges.Where(c => c.GetProperty("seatId").GetInt32() == seatId).ToList();

            public List<string> StatusesOf(int seatId) =>
                ChangesOf(seatId).Select(c => c.GetProperty("status").GetString()!).ToList();

            public List<string> TypesOf(int bookingId) =>
                BookingEvents.Where(e => e.GetProperty("bookingId").GetInt32() == bookingId)
                    .Select(e => e.GetProperty("type").GetString()!).ToList();

            public ValueTask DisposeAsync() => _connection.DisposeAsync();
        }

        private async Task<Listener> ConnectAsync(string token, int? eventId = null)
        {
            var listener = new Listener(_api.CreateHubConnection(token));
            await listener.StartAsync();
            if (eventId != null) await listener.JoinAsync(eventId.Value);
            return listener;
        }

        // Updates arrive in the background, so they are polled for. Fails after 10 seconds.
        private static async Task WaitUntilAsync(Func<bool> condition, Func<string> describe)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, $"Timed out. {describe()}");
                await Task.Delay(20);
            }
        }

        private static Task WaitForStatusAsync(Listener listener, int seatId, string status)
        {
            return WaitUntilAsync(
                () => listener.StatusesOf(seatId).LastOrDefault() == status,
                () => $"Seat {seatId} was announced as [{string.Join(", ", listener.StatusesOf(seatId))}], expected it to end as {status}.");
        }

        private static Task WaitForTypeAsync(Listener listener, int bookingId, string type)
        {
            return WaitUntilAsync(
                () => listener.TypesOf(bookingId).Contains(type),
                () => $"Booking {bookingId} got [{string.Join(", ", listener.TypesOf(bookingId))}], expected {type}.");
        }

        private static async Task<int> HoldAsync(HttpClient client, int seatId)
        {
            var response = await client.PostAsJsonAsync("/api/Booking/hold", new { seatId });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("bookingId").GetInt32();
        }

        private static string[] PropertyNames(JsonElement element)
        {
            return element.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        }

        // A correctly signed token that ran out an hour ago.
        private static string ExpiredToken(int userId)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ApiFactory.JwtKey));
            var token = new JwtSecurityToken(
                issuer: "SeatHive.Api",
                audience: "SeatHive.Client",
                claims: new[] { new Claim("sub", userId.ToString()), new Claim("role", "User") },
                notBefore: DateTime.UtcNow.AddHours(-3),
                expires: DateTime.UtcNow.AddHours(-1),
                signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        // ---- Who may connect ----

        [Fact]
        public async Task Connecting_ShouldBeRejected_WithoutAToken()
        {
            await using var connection = _api.CreateHubConnection(token: null);

            var error = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());

            Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        }

        [Fact]
        public async Task Connecting_ShouldBeRejected_WithAnExpiredToken()
        {
            var (_, _, userId) = await _api.CreateUserAsync();
            await using var connection = _api.CreateHubConnection(ExpiredToken(userId));

            var error = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());

            Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        }

        [Fact]
        public async Task Connecting_ShouldSucceed_WithAValidToken()
        {
            var (_, token, _) = await _api.CreateUserAsync();
            await using var connection = _api.CreateHubConnection(token);

            await connection.StartAsync();

            Assert.Equal(HubConnectionState.Connected, connection.State);
        }

        // A browser cannot set a header on a WebSocket, so the hub also takes the token from the query string.
        // Only the hub does: anywhere else a token in the address is ignored.
        [Fact]
        public async Task TokenInTheQueryString_ShouldBeAccepted_ByTheHubOnly()
        {
            var (_, token, _) = await _api.CreateUserAsync();
            var client = _api.CreateClient();

            var hub = await client.PostAsync($"/hubs/seats/negotiate?negotiateVersion=1&access_token={token}", null);
            var hubWithoutToken = await client.PostAsync("/hubs/seats/negotiate?negotiateVersion=1", null);
            var api = await client.GetAsync($"/api/Booking?access_token={token}");

            Assert.Equal(HttpStatusCode.OK, hub.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, hubWithoutToken.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        }

        [Fact]
        public async Task JoiningAnEvent_ShouldBeRejected_WhenTheEventDoesNotExist()
        {
            var (_, token, _) = await _api.CreateUserAsync();
            await using var listener = await ConnectAsync(token);

            var error = await Assert.ThrowsAsync<HubException>(() => listener.JoinAsync(int.MaxValue));

            Assert.Contains("event_not_found", error.Message);
        }

        // ---- Seat status for everyone watching an event ----

        [Fact]
        public async Task SeatStatus_ShouldReachEveryoneWatchingTheEvent_AsTheBookingMovesOn()
        {
            var (eventId, seatIds) = await _fixture.CreateEventAsync(seats: 2, _database);
            var (client, _, _) = await _api.CreateUserAsync();
            var (_, watcherToken, _) = await _api.CreateUserAsync();
            await using var watcher = await ConnectAsync(watcherToken, eventId);

            // Held, with the moment the hold runs out.
            var holdResponse = await client.PostAsJsonAsync("/api/Booking/hold", new { seatId = seatIds[0] });
            var hold = await holdResponse.Content.ReadFromJsonAsync<JsonElement>();
            await WaitForStatusAsync(watcher, seatIds[0], "held");
            var held = watcher.ChangesOf(seatIds[0]).First();
            Assert.Equal(hold.GetProperty("expiresAt").GetDateTime(), held.GetProperty("heldUntil").GetDateTime());
            Assert.Equal(eventId, held.GetProperty("eventId").GetInt32());

            // Released: free again.
            var bookingId = hold.GetProperty("bookingId").GetInt32();
            (await client.PostAsync($"/api/Booking/{bookingId}/release", null)).EnsureSuccessStatusCode();
            await WaitForStatusAsync(watcher, seatIds[0], "available");
            Assert.Equal(JsonValueKind.Null, watcher.ChangesOf(seatIds[0]).Last().GetProperty("heldUntil").ValueKind);

            // Paid for: booked.
            var paid = await HoldAsync(client, seatIds[1]);
            (await client.PostAsync($"/api/Booking/{paid}/confirm", null)).EnsureSuccessStatusCode();
            await WaitForStatusAsync(watcher, seatIds[1], "booked");
            Assert.Equal("held", watcher.StatusesOf(seatIds[1]).First());
            Assert.Equal(JsonValueKind.Null, watcher.ChangesOf(seatIds[1]).Last().GetProperty("heldUntil").ValueKind);
        }

        [Fact]
        public async Task SeatStatus_ShouldBecomeAvailable_WhenTheHoldRunsOut()
        {
            var (eventId, seatIds) = await _fixture.CreateEventAsync(seats: 1, _database);
            var (client, token, _) = await _api.CreateUserAsync();
            await using var watcher = await ConnectAsync(token, eventId);
            await HoldAsync(client, seatIds[0]);
            await WaitForStatusAsync(watcher, seatIds[0], "held");

            // The hold runs out and the background sweeper notices.
            _api.Clock.Advance(TimeSpan.FromMinutes(6));

            await WaitForStatusAsync(watcher, seatIds[0], "available");
        }

        [Fact]
        public async Task SeatStatusMessage_ShouldNotSayWhoHoldsTheSeat()
        {
            var (eventId, seatIds) = await _fixture.CreateEventAsync(seats: 1, _database);
            var (client, _, _) = await _api.CreateUserAsync();
            var (_, watcherToken, _) = await _api.CreateUserAsync();
            await using var watcher = await ConnectAsync(watcherToken, eventId);

            var bookingId = await HoldAsync(client, seatIds[0]);
            (await client.PostAsync($"/api/Booking/{bookingId}/confirm", null)).EnsureSuccessStatusCode();
            await WaitForStatusAsync(watcher, seatIds[0], "booked");

            // Held and booked alike: exactly these fields.
            Assert.All(watcher.ChangesOf(seatIds[0]), change =>
                Assert.Equal(new[] { "eventId", "heldUntil", "seatId", "status" }, PropertyNames(change)));
            // Someone who only watches gets nothing about the booking itself.
            Assert.Empty(watcher.BookingEvents);
        }

        [Fact]
        public async Task SeatStatus_ShouldOnlyReachThoseWatchingThatEvent()
        {
            var (eventId, seatIds) = await _fixture.CreateEventAsync(seats: 1, _database);
            var (otherEventId, _) = await _fixture.CreateEventAsync(seats: 1, _database);
            var (client, _, _) = await _api.CreateUserAsync();
            var (_, token, _) = await _api.CreateUserAsync();
            await using var watching = await ConnectAsync(token, eventId);
            await using var watchingAnotherEvent = await ConnectAsync(token, otherEventId);
            await using var watchingNothing = await ConnectAsync(token);
            await using var left = await ConnectAsync(token, eventId);
            await left.LeaveAsync(eventId);

            await HoldAsync(client, seatIds[0]);
            await WaitForStatusAsync(watching, seatIds[0], "held");

            Assert.Empty(watchingAnotherEvent.SeatChanges);
            Assert.Empty(watchingNothing.SeatChanges);
            Assert.Empty(left.SeatChanges);
        }

        [Fact]
        public async Task SeatStatus_ShouldNotBeAnnounced_ForAChangeThatWasRolledBack()
        {
            var (eventId, seatIds) = await _fixture.CreateEventAsync(seats: 2, _database);
            var (client, token, userId) = await _api.CreateUserAsync();
            await using var watcher = await ConnectAsync(token, eventId);

            // A "seat held" stored in the outbox by a transaction that is then rolled back...
            using (var scope = _api.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var publisher = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
                var now = _api.Clock.GetUtcNow().UtcDateTime;

                await using var transaction = await db.Database.BeginTransactionAsync();
                await publisher.Publish(new SeatHeld(-1, seatIds[0], userId, now, now.AddMinutes(5)));
                await db.SaveChangesAsync();
                await transaction.RollbackAsync();
            }

            // ...and a real hold afterwards. Once that one is announced, the first would have been too.
            await HoldAsync(client, seatIds[1]);
            await WaitForStatusAsync(watcher, seatIds[1], "held");

            Assert.Empty(watcher.ChangesOf(seatIds[0]));
        }

        // ---- The owner's own booking ----

        [Fact]
        public async Task BookingEvents_ShouldReachTheOwner_OnEveryConnection_AndNobodyElse()
        {
            var (eventId, seatIds) = await _fixture.CreateEventAsync(seats: 1, _database);
            var (owner, ownerToken, _) = await _api.CreateUserAsync();
            var (_, otherToken, _) = await _api.CreateUserAsync();
            await using var ownerConnection = await ConnectAsync(ownerToken, eventId);
            await using var ownerSecondConnection = await ConnectAsync(ownerToken);
            await using var other = await ConnectAsync(otherToken, eventId);

            var bookingId = await HoldAsync(owner, seatIds[0]);
            (await owner.PostAsync($"/api/Booking/{bookingId}/confirm", null)).EnsureSuccessStatusCode();

            var expected = new[] { "seatHeld", "paymentRequested", "paymentSucceeded", "bookingConfirmed", "notificationSent" };
            foreach (var connection in new[] { ownerConnection, ownerSecondConnection })
            {
                foreach (var type in expected) await WaitForTypeAsync(connection, bookingId, type);
                Assert.Equal(expected.Order(), connection.TypesOf(bookingId).Distinct().Order());
            }

            // The other user watches the same event: the seat, yes; the booking, no.
            await WaitForStatusAsync(other, seatIds[0], "booked");
            Assert.Empty(other.BookingEvents);

            // Exactly these fields, and no user id.
            var sent = ownerConnection.BookingEvents.First(e => e.GetProperty("type").GetString() == "paymentSucceeded");
            Assert.Equal(
                new[] { "bookingId", "detail", "eventId", "occurredAt", "paymentId", "seatId", "simulated", "type" },
                PropertyNames(sent));
            Assert.True(sent.GetProperty("simulated").GetBoolean());
            Assert.Equal(seatIds[0], sent.GetProperty("seatId").GetInt32());

            // The same events, with the same ids, as the history of the booking.
            // The history is written by a consumer of its own, so it may be a moment behind the live connection.
            var history = default(JsonElement);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                history = await owner.GetFromJsonAsync<JsonElement>($"/api/Booking/{bookingId}/history");
                if (history.GetProperty("totalCount").GetInt32() >= expected.Length) break;

                Assert.True(DateTime.UtcNow < deadline, "The history did not get all five events.");
                await Task.Delay(20);
            }
            var historyIds = history.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("eventId").GetGuid()).Order();
            var liveIds = ownerConnection.BookingEvents.Select(e => e.GetProperty("eventId").GetGuid()).Distinct().Order();
            Assert.Equal(historyIds, liveIds);
        }

        [Fact]
        public async Task BookingEvents_ShouldTellTheOwner_WhenThePaymentFailed()
        {
            var (_, seatIds) = await _fixture.CreateEventAsync(seats: 1, _database);
            var (owner, token, _) = await _api.CreateUserAsync();
            await using var connection = await ConnectAsync(token);

            var bookingId = await HoldAsync(owner, seatIds[0]);
            (await owner.PostAsJsonAsync($"/api/Booking/{bookingId}/confirm", new { simulatePaymentFailure = true })).EnsureSuccessStatusCode();

            await WaitForTypeAsync(connection, bookingId, "paymentFailed");
            var failed = connection.BookingEvents.First(e => e.GetProperty("type").GetString() == "paymentFailed");
            Assert.Equal("forced", failed.GetProperty("detail").GetString());
            Assert.DoesNotContain("bookingConfirmed", connection.TypesOf(bookingId));
        }

        // ---- Reset ----

        [Fact]
        public async Task Reset_ShouldBeAnnouncedToEveryConnection()
        {
            var (_, token, _) = await _api.CreateUserAsync();
            await using var connection = await ConnectAsync(token);
            var admin = await _api.CreateAdminClientAsync();

            (await admin.PostAsync("/api/Setup/create-data", null)).EnsureSuccessStatusCode();

            await WaitUntilAsync(() => connection.Resets > 0, () => "The reset was not announced.");
        }
    }
}
