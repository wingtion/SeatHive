using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SeatHive.Api.Models;
using SeatHive.Api.Services;
using SeatHive.Shared.Events;

namespace SeatHive.Tests.Integration.Api
{
    // A reset of the demo data (the admin's or the nightly one) may come while a race is running, or while its
    // winner still holds the seat. Nothing may fail, and what the race reports must be what the database holds.
    [Collection(TestCollections.DemoReset)]
    [Trait(TestCategories.Trait, TestCategories.Api)]
    public class RaceDuringResetTests
    {
        private const string ResetUrl = "/api/Setup/create-data";
        private const string RaceUrl = "/api/Simulation/simulate-concurrency";
        private static readonly TimeSpan WinnerHold = TimeSpan.FromSeconds(10);

        private readonly ContainersFixture _fixture;

        public RaceDuringResetTests(ContainersFixture fixture)
        {
            _fixture = fixture;
        }

        private static async Task ResetAsync(HttpClient admin)
        {
            var response = await admin.PostAsync(ResetUrl, null);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"Reset: {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        private static async Task<JsonElement> RaceOkAsync(Task<HttpResponseMessage> racing)
        {
            var response = await racing;
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"Race: {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        private static List<string?> Outcomes(JsonElement race) =>
            race.GetProperty("attempts").EnumerateArray().Select(a => a.GetProperty("outcome").GetString()).ToList();

        private async Task<List<Booking>> ActiveBookingsAsync()
        {
            await using var db = _fixture.CreateContext();
            return await db.Bookings.AsNoTracking().Include(b => b.User)
                .Where(b => b.Status == BookingStatus.Held || b.Status == BookingStatus.PaymentPending || b.Status == BookingStatus.Confirmed)
                .ToListAsync();
        }

        // The racers are stopped at the lock, the reset runs, then the racers go on: they race for seat 1 of the
        // new data. The reset is announced first, then the race, and the race's winner is the one hold there is.
        [Fact]
        public async Task Reset_WhileRacersAreAtTheLock_ShouldLeaveARaceThatIsTrue()
        {
            var ct = TestContext.Current.CancellationToken;
            var gate = new GatedLockService();
            await using var api = new ApiFactory(_fixture, configureServices: services => services.AddSingleton<IRedisLockService>(gate));
            var admin = await api.CreateAdminClientAsync();
            var client = await api.CreateUserClientAsync();
            await ResetAsync(admin);
            await WaitForAsync(() => api.CountDelivered<DemoDataReset>(_ => true) == 1, "the first reset");

            var racing = client.PostAsJsonAsync(RaceUrl, new { seatId = 1, racers = 3 }, ct);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

            await ResetAsync(admin);
            await WaitForAsync(() => api.CountDelivered<DemoDataReset>(_ => true) == 2, "the reset during the race");
            gate.Gate.TrySetResult();

            // The report is whole: every racer has an outcome, none failed, one won.
            var race = await RaceOkAsync(racing);
            Assert.Equal(new[] { "rejected", "rejected", "won" }, Outcomes(race).Order());
            var winnerBookingId = race.GetProperty("winner").GetProperty("bookingId").GetInt32();

            // The only active booking is the winner's, on the seat the report names, and it belongs to a racer.
            var active = Assert.Single(await ActiveBookingsAsync());
            Assert.Equal(winnerBookingId, active.Id);
            Assert.Equal(1, active.SeatId);
            Assert.Equal(Roles.Racer, active.User!.Role);

            // Announced in the order it happened: the reset, then the race.
            var finished = await api.WaitForDeliveryAsync<RaceFinished>(e => e.RaceId == race.GetProperty("raceId").GetGuid());
            var announced = api.Services.GetRequiredService<EventLog>().Of<object>(e => e is DemoDataReset or RaceFinished);
            Assert.Equal(new[] { typeof(DemoDataReset), typeof(DemoDataReset), typeof(RaceFinished) }, announced.Select(e => e.GetType()));
            Assert.Equal(winnerBookingId, finished.Winner!.BookingId);

            // And the winner lets go at its time, as after any race: no hold is left behind.
            api.Clock.Advance(WinnerHold);
            await _fixture.WaitForBookingStatusAsync(winnerBookingId, BookingStatus.Released);
            Assert.Empty(await ActiveBookingsAsync());
        }

        // The reset deletes the winner's booking before its release is due. The release then finds nothing,
        // which is not an error, and the next race and its release work as always.
        [Fact]
        public async Task Reset_WhileTheWinnerStillHoldsTheSeat_ShouldNotBreakTheNextRace()
        {
            var ct = TestContext.Current.CancellationToken;
            await using var api = new ApiFactory(_fixture);
            var admin = await api.CreateAdminClientAsync();
            var client = await api.CreateUserClientAsync();
            await ResetAsync(admin);

            var first = await RaceOkAsync(client.PostAsJsonAsync(RaceUrl, new { seatId = 1, racers = 3 }, ct));
            Assert.Equal(1, first.GetProperty("winners").GetInt32());

            await ResetAsync(admin);
            Assert.Empty(await ActiveBookingsAsync());

            // The first winner's release comes due and has nothing to release.
            api.Clock.Advance(WinnerHold);

            var second = await RaceOkAsync(client.PostAsJsonAsync(RaceUrl, new { seatId = 1, racers = 3 }, ct));
            Assert.Equal(1, second.GetProperty("winners").GetInt32());
            var bookingId = second.GetProperty("winner").GetProperty("bookingId").GetInt32();

            api.Clock.Advance(WinnerHold);
            await _fixture.WaitForBookingStatusAsync(bookingId, BookingStatus.Released);
        }

        // No gate here: resets and races start at the same moment, again and again, with every racer going to the
        // database at once (no seat lock). However they interleave, no request may fail and no racer may end in an error.
        // Before the reset gave way to requests (see DemoDataResetter), this ended in "deadlock detected" within a few rounds.
        [Fact]
        public async Task Resets_AndRaces_AtTheSameMoment_ShouldNeverFail()
        {
            var ct = TestContext.Current.CancellationToken;
            await using var api = new ApiFactory(_fixture, new Dictionary<string, string?>
            {
                ["ConnectionStrings__Redis"] = RedisLockServiceTests.UnreachableRedis,
                ["RateLimiting__Simulation__PermitLimit"] = "100000"
            });
            var admin = await api.CreateAdminClientAsync();
            var client = await api.CreateUserClientAsync();
            await ResetAsync(admin);

            for (var round = 0; round < 25; round++)
            {
                var racing = client.PostAsJsonAsync(RaceUrl, new { racers = 20 }, ct);
                var resetting = ResetAsync(admin);

                var race = await RaceOkAsync(racing);
                await resetting;

                Assert.DoesNotContain("error", Outcomes(race));
                Assert.True(race.GetProperty("winners").GetInt32() <= 1, $"Round {round}: more than one winner.");
            }

            // What is left is whole: the demo data, and at most the last winner's hold.
            await using var db = _fixture.CreateContext();
            Assert.Equal(100, await db.Seats.CountAsync(ct));
            Assert.True((await ActiveBookingsAsync()).Count <= 1);
        }

        private static async Task WaitForAsync(Func<bool> condition, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
                await Task.Delay(20);
            }
        }

        // Stops every racer at the seat lock until the test opens the gate; then everyone gets a "lock"
        // and the database lets only one hold through.
        private sealed class GatedLockService : IRedisLockService
        {
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
    }
}
