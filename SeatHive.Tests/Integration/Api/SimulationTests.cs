using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SeatHive.Api.Models;
using SeatHive.Api.Services;
using SeatHive.Shared.Events;

namespace SeatHive.Tests.Integration.Api
{
    // The race simulation: racers, each a user of its own, try to hold one seat at the same moment.
    // Exactly one may win; every attempt says whether it got the lock and why it lost.
    [Collection(TestCollections.Simulation)]
    [Trait(TestCategories.Trait, TestCategories.Api)]
    public class SimulationTests
    {
        private const string RaceUrl = "/api/Simulation/simulate-concurrency";
        private const string RacerDomain = "racers.seathive.invalid";
        private static readonly TimeSpan WinnerHold = TimeSpan.FromSeconds(10);

        private readonly ContainersFixture _fixture;
        private readonly ApiFactory _api;

        public SimulationTests(ContainersFixture fixture)
        {
            _fixture = fixture;
            _api = fixture.Api;
        }

        private static Task<HttpResponseMessage> RaceAsync(HttpClient client, int? seatId = null, int? racers = null)
        {
            return client.PostAsJsonAsync(RaceUrl, new { seatId, racers });
        }

        private static async Task<JsonElement> RaceOkAsync(HttpClient client, int? seatId = null, int? racers = null)
        {
            var response = await RaceAsync(client, seatId, racers);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        // The seat as everyone sees it (GET /api/events/{id}/seats).
        private static async Task<string> SeatStatusAsync(HttpClient client, int eventId, int seatId)
        {
            var page = await client.GetFromJsonAsync<JsonElement>($"/api/Events/{eventId}/seats?pageSize=500");
            return page.GetProperty("items").EnumerateArray()
                .Single(s => s.GetProperty("seatId").GetInt32() == seatId)
                .GetProperty("status").GetString()!;
        }

        private static List<JsonElement> Attempts(JsonElement race) => race.GetProperty("attempts").EnumerateArray().ToList();

        private static int WinnerBookingId(JsonElement race) => race.GetProperty("winner").GetProperty("bookingId").GetInt32();

        // ---- Who may race ----

        [Fact]
        public async Task Race_ShouldReturn401_WithoutAToken()
        {
            var response = await RaceAsync(_api.CreateClient());

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // Not only admins: whoever evaluates the system can start a race themselves.
        [Fact]
        public async Task Race_ShouldBeOpen_ToEverySignedInUser()
        {
            var (_, seatIds) = await _fixture.CreateEventAsync(seats: 1);
            var client = await _api.CreateUserClientAsync();

            var race = await RaceOkAsync(client, seatIds[0], racers: 5);

            Assert.Equal(1, race.GetProperty("winners").GetInt32());
        }

        // ---- The race ----

        [Fact]
        public async Task Race_ShouldHaveExactlyOneWinner_AndSayWhyEveryoneElseLost()
        {
            var (eventId, seatIds) = await _fixture.CreateEventAsync(seats: 1);
            var client = await _api.CreateUserClientAsync();

            var race = await RaceOkAsync(client, seatIds[0], racers: 20);

            Assert.Equal(seatIds[0], race.GetProperty("seatId").GetInt32());
            Assert.Equal(eventId, race.GetProperty("eventId").GetInt32());
            Assert.Equal(20, race.GetProperty("racers").GetInt32());

            var attempts = Attempts(race);
            Assert.Equal(20, attempts.Count);
            Assert.Equal(20, attempts.Select(a => a.GetProperty("racer").GetInt32()).Distinct().Count());

            var winners = attempts.Where(a => a.GetProperty("outcome").GetString() == "won").ToList();
            var winner = Assert.Single(winners);
            Assert.Equal(1, race.GetProperty("winners").GetInt32());
            Assert.Equal(JsonValueKind.Null, winner.GetProperty("code").ValueKind);
            Assert.Equal("acquired", winner.GetProperty("lock").GetString());
            Assert.Equal(winner.GetProperty("racer").GetInt32(), race.GetProperty("winner").GetProperty("racer").GetInt32());

            // Every loser lost either at the lock (someone else had it) or at the database, after the lock (the seat was taken).
            foreach (var loser in attempts.Where(a => a.GetProperty("outcome").GetString() == "rejected"))
            {
                var (code, lockState) = (loser.GetProperty("code").GetString(), loser.GetProperty("lock").GetString());
                Assert.True(
                    (code == "seat_locked" && lockState == "busy") || (code == "seat_held" && lockState == "acquired"),
                    $"Racer {loser.GetProperty("racer").GetInt32()}: code {code}, lock {lockState}.");
                Assert.True(loser.GetProperty("finishedAtMs").GetInt64() >= loser.GetProperty("startedAtMs").GetInt64());
            }

            // The response names racers by number, never by user id or email.
            var text = race.GetRawText();
            Assert.DoesNotContain(RacerDomain, text);
            Assert.DoesNotContain("userId", text);
        }

        // Without Redis nobody gets a lock and nobody is told "locked": the database alone decides, still one winner.
        [Fact]
        public async Task Race_ShouldStillHaveOneWinner_WhenRedisIsDown()
        {
            var (_, seatIds) = await _fixture.CreateEventAsync(seats: 1);
            await using var api = new ApiFactory(_fixture, new Dictionary<string, string?>
            {
                ["ConnectionStrings__Redis"] = RedisLockServiceTests.UnreachableRedis
            });
            var client = await api.CreateUserClientAsync();

            var race = await RaceOkAsync(client, seatIds[0], racers: 10);

            var attempts = Attempts(race);
            Assert.All(attempts, a => Assert.Equal("unavailable", a.GetProperty("lock").GetString()));
            Assert.Single(attempts, a => a.GetProperty("outcome").GetString() == "won");
            Assert.All(attempts.Where(a => a.GetProperty("outcome").GetString() == "rejected"),
                a => Assert.Equal("seat_held", a.GetProperty("code").GetString()));
        }

        // The old simulation always used seat #1 and never let go of it, so a second run reported no winner.
        [Fact]
        public async Task Races_OneAfterAnother_ShouldEachHaveOneWinner()
        {
            var (_, seatIds) = await _fixture.CreateEventAsync(seats: 2);
            var client = await _api.CreateUserClientAsync();

            var first = await RaceOkAsync(client, seatIds[0], racers: 5);
            var second = await RaceOkAsync(client, seatIds[1], racers: 5);

            Assert.Equal(1, first.GetProperty("winners").GetInt32());
            Assert.Equal(1, second.GetProperty("winners").GetInt32());
        }

        [Fact]
        public async Task Race_WithoutASeat_ShouldPickAFreeOne()
        {
            await _fixture.CreateEventAsync(seats: 1);
            var client = await _api.CreateUserClientAsync();

            var race = await RaceOkAsync(client, seatId: null, racers: 3);

            Assert.Equal(1, race.GetProperty("winners").GetInt32());
            Assert.Equal(3, Attempts(race).Count);
        }

        [Fact]
        public async Task Race_ShouldUseTwentyRacers_ByDefault()
        {
            var (_, seatIds) = await _fixture.CreateEventAsync(seats: 1);
            var client = await _api.CreateUserClientAsync();

            var race = await RaceOkAsync(client, seatIds[0]);

            Assert.Equal(20, Attempts(race).Count);
        }

        // ---- The winner's hold, and letting it go ----

        // The winner keeps the seat for a while, so the hold can be seen, and then gives it up through the real
        // release (HoldReleased). Time here is the host's clock, which only moves when the test moves it.
        [Fact]
        public async Task WinnersHold_ShouldBeReleased_AfterTheConfiguredTime()
        {
            var (eventId, seatIds) = await _fixture.CreateEventAsync(seats: 1);
            var client = await _api.CreateUserClientAsync();

            var race = await RaceOkAsync(client, seatIds[0], racers: 5);
            var bookingId = WinnerBookingId(race);
            var releasesAt = race.GetProperty("winner").GetProperty("releasesAt").GetDateTime();
            Assert.Equal(race.GetProperty("startedAt").GetDateTime() + WinnerHold, releasesAt);
            Assert.Equal("held", await SeatStatusAsync(client, eventId, seatIds[0]));

            _api.Clock.Advance(WinnerHold - TimeSpan.FromSeconds(1));
            Assert.Equal("held", await SeatStatusAsync(client, eventId, seatIds[0]));
            Assert.Equal(BookingStatus.Held, (await _fixture.ReadBookingAsync(bookingId)).Status);

            _api.Clock.Advance(TimeSpan.FromSeconds(1));
            await _api.WaitForDeliveryAsync<HoldReleased>(e => e.BookingId == bookingId);
            Assert.Equal(BookingStatus.Released, (await _fixture.ReadBookingAsync(bookingId)).Status);
            Assert.Equal("available", await SeatStatusAsync(client, eventId, seatIds[0]));
        }

        [Fact]
        public async Task WinnersHold_ShouldLastAsLongAsConfigured()
        {
            var (_, seatIds) = await _fixture.CreateEventAsync(seats: 1);
            await using var api = new ApiFactory(_fixture, new Dictionary<string, string?> { ["Simulation__WinnerHoldSeconds"] = "3" });
            var client = await api.CreateUserClientAsync();

            var race = await RaceOkAsync(client, seatIds[0], racers: 3);
            var releasesAt = race.GetProperty("winner").GetProperty("releasesAt").GetDateTime();
            Assert.Equal(race.GetProperty("startedAt").GetDateTime() + TimeSpan.FromSeconds(3), releasesAt);

            api.Clock.Advance(TimeSpan.FromSeconds(3));

            await _fixture.WaitForBookingStatusAsync(WinnerBookingId(race), BookingStatus.Released);
        }

        // While the winner still holds the seat, a new race on it would only report that nobody won.
        // It is refused instead, and says when the seat comes free.
        [Fact]
        public async Task Race_ShouldBeRefused_WhileTheLastWinnerStillHoldsTheSeat()
        {
            var (_, seatIds) = await _fixture.CreateEventAsync(seats: 1);
            var client = await _api.CreateUserClientAsync();
            var race = await RaceOkAsync(client, seatIds[0], racers: 3);
            var bookingId = WinnerBookingId(race);

            var refused = await RaceAsync(client, seatIds[0], racers: 3);

            // The body is read once: the code and when the seat comes free.
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
            var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(ErrorCodes.SeatHeldByRace, body.GetProperty("code").GetString());
            Assert.Equal(race.GetProperty("winner").GetProperty("releasesAt").GetDateTime(), body.GetProperty("releasesAt").GetDateTime());

            // Once the winner has let go, the seat can be raced for again.
            _api.Clock.Advance(WinnerHold);
            await _fixture.WaitForBookingStatusAsync(bookingId, BookingStatus.Released);
            var again = await RaceOkAsync(client, seatIds[0], racers: 3);
            Assert.Equal(1, again.GetProperty("winners").GetInt32());
        }

        // ---- When a race cannot start ----

        [Fact]
        public async Task Race_ShouldBeRefused_WhenAUserHoldsTheSeat()
        {
            var (_, seatIds) = await _fixture.CreateEventAsync(seats: 1);
            var client = await _api.CreateUserClientAsync();
            (await client.PostAsJsonAsync("/api/Booking/hold", new { seatId = seatIds[0] })).EnsureSuccessStatusCode();

            var response = await RaceAsync(client, seatIds[0], racers: 3);

            await ProblemAssert.HasCodeAsync(response, HttpStatusCode.Conflict, ErrorCodes.SeatHeld);
        }

        [Fact]
        public async Task Race_ShouldBeRefused_WhenTheSeatIsBooked()
        {
            var (_, seatIds) = await _fixture.CreateEventAsync(seats: 1);
            var client = await _api.CreateUserClientAsync();
            var hold = await client.PostAsJsonAsync("/api/Booking/hold", new { seatId = seatIds[0] });
            var bookingId = (await hold.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("bookingId").GetInt32();
            await _fixture.ConfirmThroughPaymentAsync(client, bookingId);

            var response = await RaceAsync(client, seatIds[0], racers: 3);

            await ProblemAssert.HasCodeAsync(response, HttpStatusCode.Conflict, ErrorCodes.SeatAlreadyBooked);
        }

        [Fact]
        public async Task Race_ShouldReturn404_ForASeatThatDoesNotExist()
        {
            var client = await _api.CreateUserClientAsync();

            var response = await RaceAsync(client, int.MaxValue, racers: 3);

            await ProblemAssert.HasCodeAsync(response, HttpStatusCode.NotFound, ErrorCodes.SeatNotFound);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(51)]
        public async Task Race_ShouldReturn400_ForTooFewOrTooManyRacers(int racers)
        {
            var (_, seatIds) = await _fixture.CreateEventAsync(seats: 1);
            var client = await _api.CreateUserClientAsync();

            var response = await RaceAsync(client, seatIds[0], racers);

            await ProblemAssert.HasCodeAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        }

        // A racer still holding a seat from an earlier race is left out; when too few are free, the race does not
        // start rather than letting a racer lose for a reason that has nothing to do with the seat (its hold limit).
        [Fact]
        public async Task Race_ShouldBeRefused_WhenTooFewRacersAreFree()
        {
            var (_, seatIds) = await _fixture.CreateEventAsync(seats: 2);
            var client = await _api.CreateUserClientAsync();
            await RaceOkAsync(client, seatIds[0], racers: 3);

            // The winner of the first race still holds its seat, so not all 50 are free.
            var response = await RaceAsync(client, seatIds[1], racers: 50);

            await ProblemAssert.HasCodeAsync(response, HttpStatusCode.Conflict, ErrorCodes.RacersBusy);
        }

        // One race at a time. Here the first race is stopped at the lock until the test lets it go,
        // so the second request certainly arrives while the first is running.
        [Fact]
        public async Task SecondRace_ShouldBeRefused_WhileOneIsRunning()
        {
            var (_, seatIds) = await _fixture.CreateEventAsync(seats: 2);
            var gatedLock = new GatedLockService();
            await using var api = _api.WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services => services.AddSingleton<IRedisLockService>(gatedLock)));
            var (_, token, _) = await _api.CreateUserAsync();
            var client = ApiFactory.Authorize(api.CreateClient(), token);

            var first = RaceAsync(client, seatIds[0], racers: 3);
            await gatedLock.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var second = await RaceAsync(client, seatIds[1], racers: 3);
            gatedLock.Gate.TrySetResult();

            await ProblemAssert.HasCodeAsync(second, HttpStatusCode.Conflict, ErrorCodes.RaceInProgress);
            Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        }

        private sealed class GatedLockService : IRedisLockService
        {
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            // Everyone gets a "lock"; the database still lets only one hold through.
            public async Task<IAsyncDisposable?> AcquireLockAsync(string key, TimeSpan expiry)
            {
                Entered.TrySetResult();
                await Gate.Task;
                return new NoLock();
            }

            private sealed class NoLock : IAsyncDisposable
            {
                public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            }
        }

        [Fact]
        public async Task Race_ShouldBeRateLimited_PerUser()
        {
            var (_, seatIds) = await _fixture.CreateEventAsync(seats: 2);
            await using var api = new ApiFactory(_fixture, new Dictionary<string, string?> { ["RateLimiting__Simulation__PermitLimit"] = "1" });
            var client = await api.CreateUserClientAsync();

            Assert.Equal(HttpStatusCode.OK, (await RaceAsync(client, seatIds[0], racers: 3)).StatusCode);
            var limited = await RaceAsync(client, seatIds[1], racers: 3);

            await ProblemAssert.HasCodeAsync(limited, HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        }

        // ---- The racers' accounts ----

        // Racers are users of their own that nobody can sign in as, and nobody can register in their place.
        [Fact]
        public async Task RacerAccounts_ShouldNotBeUsable_BySomeoneElse()
        {
            var (_, seatIds) = await _fixture.CreateEventAsync(seats: 1);
            var client = await _api.CreateUserClientAsync();
            await RaceOkAsync(client, seatIds[0], racers: 2);

            await using var db = _fixture.CreateContext();
            var racers = await db.Users.AsNoTracking().Where(u => u.Email.EndsWith("@" + RacerDomain)).ToListAsync();
            Assert.Equal(50, racers.Count);
            Assert.All(racers, r => Assert.Equal(Roles.Racer, r.Role));

            var anonymous = _api.CreateClient();
            var login = await anonymous.PostAsJsonAsync("/api/Auth/login", new { email = racers[0].Email, password = "Passw0rd!" });
            await ProblemAssert.HasCodeAsync(login, HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);

            var register = await ApiFactory.RegisterAsync(anonymous, $"someone-{Guid.NewGuid():N}@{RacerDomain}", "Passw0rd!");
            await ProblemAssert.HasCodeAsync(register, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        }

        // ---- Live ----

        // Everyone watching the event sees the race, with the same attempts the starter got back.
        [Fact]
        public async Task RaceResult_ShouldReachEveryoneWatchingTheEvent_AndNobodyElse()
        {
            var (eventId, seatIds) = await _fixture.CreateEventAsync(seats: 1);
            var (otherEventId, _) = await _fixture.CreateEventAsync(seats: 1);
            var (client, _, _) = await _api.CreateUserAsync();
            var (_, watcherToken, _) = await _api.CreateUserAsync();

            var watching = new ConcurrentQueue<JsonElement>();
            var elsewhere = new ConcurrentQueue<JsonElement>();
            await using var watcher = await WatchAsync(watcherToken, eventId, watching);
            await using var other = await WatchAsync(watcherToken, otherEventId, elsewhere);

            var race = await RaceOkAsync(client, seatIds[0], racers: 5);

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (watching.IsEmpty)
            {
                Assert.True(DateTime.UtcNow < deadline, "raceFinished did not arrive.");
                await Task.Delay(20);
            }

            var sent = Assert.Single(watching);
            Assert.Equal(race.GetProperty("raceId").GetGuid(), sent.GetProperty("raceId").GetGuid());
            Assert.Equal(seatIds[0], sent.GetProperty("seatId").GetInt32());
            Assert.Equal(Describe(race), Describe(sent));
            Assert.Empty(elsewhere);
        }

        private static string Describe(JsonElement race)
        {
            return string.Join(";", Attempts(race).Select(a =>
                $"{a.GetProperty("racer").GetInt32()}:{a.GetProperty("outcome").GetString()}:{a.GetProperty("code")}:{a.GetProperty("lock").GetString()}"));
        }

        private async Task<HubConnection> WatchAsync(string token, int eventId, ConcurrentQueue<JsonElement> received)
        {
            var connection = _api.CreateHubConnection(token);
            connection.On<JsonElement>("raceFinished", received.Enqueue);
            await connection.StartAsync();
            var joined = await connection.InvokeAsync<JsonElement>("JoinEvent", eventId);
            Assert.True(joined.GetProperty("joined").GetBoolean());
            return connection;
        }
    }
}
