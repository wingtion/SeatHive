using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SeatHive.Api.Services;
using SeatHive.Shared.Events;

namespace SeatHive.Tests.Integration.Api
{
    [Collection(TestCollections.DemoReset)]
    [Trait(TestCategories.Trait, TestCategories.Api)]
    public class DemoResetTests
    {
        private const string ResetUrl = "/api/Setup/create-data";

        private readonly ContainersFixture _fixture;

        public DemoResetTests(ContainersFixture fixture)
        {
            _fixture = fixture;
        }

        private ApiFactory CreateApi(string? dailyAtUtc)
        {
            return new ApiFactory(_fixture, new Dictionary<string, string?> { ["DemoReset__DailyAtUtc"] = dailyAtUtc });
        }

        [Fact]
        public async Task NightlyReset_ShouldResetTheDemoData_AtTheConfiguredTime_EveryDay()
        {
            var ct = TestContext.Current.CancellationToken;
            // The host's clock starts at the real time, so this is about an hour away on it.
            var dailyAt = DateTimeOffset.UtcNow.AddHours(1).ToString("HH:mm", CultureInfo.InvariantCulture);
            await using var api = CreateApi(dailyAt);

            var client = await api.CreateUserClientAsync();
            var (_, seatIds) = await _fixture.CreateEventAsync(seats: 1);
            (await client.PostAsJsonAsync("/api/Booking/hold", new { seatId = seatIds[0] }, ct)).EnsureSuccessStatusCode();

            // Not before its time.
            api.Clock.Advance(TimeSpan.FromMinutes(30));
            Assert.Equal(0, api.CountDelivered<DemoDataReset>(_ => true));
            await using (var before = _fixture.CreateContext())
            {
                Assert.Equal(1, await before.Bookings.CountAsync(b => b.SeatId == seatIds[0], ct));
            }

            // The reset, and then the wait for the next night.
            var waitingForTheNextNight = api.Clock.NextWaitAsync();
            api.Clock.Advance(TimeSpan.FromMinutes(31));
            await api.WaitForDeliveryAsync<DemoDataReset>(_ => true);

            await using (var after = _fixture.CreateContext())
            {
                Assert.Equal(0, await after.Bookings.CountAsync(ct));
                Assert.Equal(100, await after.Seats.CountAsync(ct));
                Assert.Equal(1, await after.Events.CountAsync(ct));
            }

            await waitingForTheNextNight.WaitAsync(TimeSpan.FromSeconds(10), ct);
            api.Clock.Advance(TimeSpan.FromHours(24));
            await WaitForResetsAsync(api, 2);
        }

        [Fact]
        public async Task Reset_ShouldRemoveGuestsWhoseTokenRanOut_AndKeepTheOthers()
        {
            var ct = TestContext.Current.CancellationToken;
            await using var api = CreateApi(dailyAtUtc: null);
            var (_, seatIds) = await _fixture.CreateEventAsync(seats: 1);

            // An old guest that even has a booking, a guest whose token still works, and a registered user.
            var oldToken = await GuestTests.CreateGuestAsync(api.CreateClient());
            var oldGuest = ApiFactory.Authorize(api.CreateClient(), oldToken);
            (await oldGuest.PostAsJsonAsync("/api/Booking/hold", new { seatId = seatIds[0] }, ct)).EnsureSuccessStatusCode();

            api.Clock.Advance(GuestAccounts.KeptFor + TimeSpan.FromMinutes(1));

            var newToken = await GuestTests.CreateGuestAsync(api.CreateClient());
            var (_, _, userId) = await api.CreateUserAsync();

            var admin = await api.CreateAdminClientAsync();
            (await admin.PostAsync(ResetUrl, null, ct)).EnsureSuccessStatusCode();

            var oldId = int.Parse(GuestTests.ReadClaim(oldToken, "sub")!);
            var newId = int.Parse(GuestTests.ReadClaim(newToken, "sub")!);
            await using var db = _fixture.CreateContext();
            var left = await db.Users.AsNoTracking()
                .Where(u => u.Id == oldId || u.Id == newId || u.Id == userId)
                .Select(u => u.Id)
                .ToListAsync(ct);
            Assert.Equal(new[] { newId, userId }.Order(), left.Order());

            // The guest that was kept can go on: the token still belongs to a user.
            var newGuest = ApiFactory.Authorize(api.CreateClient(), newToken);
            var hold = await newGuest.PostAsJsonAsync("/api/Booking/hold", new { seatId = 1 }, ct);
            Assert.Equal(HttpStatusCode.OK, hold.StatusCode);
        }

        [Fact]
        public async Task Startup_ShouldFailWithClearError_WhenTheResetTimeIsNotATimeOfDay()
        {
            await using var api = CreateApi("25:99");

            var error = Assert.ThrowsAny<Exception>(() => api.CreateClient());

            Assert.Contains("DemoReset:DailyAtUtc", error.ToString());
        }

        [Theory]
        [InlineData("2026-10-03T02:00:00Z", "03:00", 1)]
        [InlineData("2026-10-03T04:30:00Z", "03:00", 22.5)]
        // At exactly that time the reset has just run; the next one is a day away.
        [InlineData("2026-10-03T03:00:00Z", "03:00", 24)]
        [InlineData("2026-10-03T23:59:00Z", "00:00", 1.0 / 60)]
        public void TimeUntilNext_ShouldBeTheTimeToTheNextOccurrence(string now, string dailyAt, double expectedHours)
        {
            var wait = NightlyDemoReset.TimeUntilNext(
                DateTimeOffset.Parse(now, CultureInfo.InvariantCulture),
                NightlyDemoReset.ParseDailyAt(dailyAt)!.Value);

            Assert.Equal(TimeSpan.FromHours(expectedHours), wait);
        }

        // Delivery happens in the background, so it is polled for. Fails after 10 seconds.
        private static async Task WaitForResetsAsync(ApiFactory api, int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (api.CountDelivered<DemoDataReset>(_ => true) < count)
            {
                Assert.True(DateTime.UtcNow < deadline, $"Fewer than {count} resets were announced.");
                await Task.Delay(20);
            }
        }
    }
}
