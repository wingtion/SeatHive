using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SeatHive.Api.Models;
using SeatHive.Api.Services;

namespace SeatHive.Tests.Integration.Api
{
    [Collection(ContainersCollection.Name)]
    public class ReadEndpointsTests
    {
        private readonly ContainersFixture _fixture;

        public ReadEndpointsTests(ContainersFixture fixture)
        {
            _fixture = fixture;
        }

        private HttpClient Anonymous() => _fixture.Api.CreateClient();

        private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
        {
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        private static async Task<JsonElement> HoldAsync(HttpClient client, int seatId)
        {
            var response = await client.PostAsJsonAsync("/api/Booking/hold", new { seatId });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        private static async Task<int> HoldIdAsync(HttpClient client, int seatId)
        {
            return (await HoldAsync(client, seatId)).GetProperty("bookingId").GetInt32();
        }

        // The one seat of an event, as the anonymous seat endpoint shows it.
        private async Task<JsonElement> ReadSeatAsync(int eventId, int seatId)
        {
            var page = await GetJsonAsync(Anonymous(), $"/api/Events/{eventId}/seats");
            return page.GetProperty("items").EnumerateArray().Single(s => s.GetProperty("seatId").GetInt32() == seatId);
        }

        private static string[] PropertyNames(JsonElement element)
        {
            return element.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        }

        // ---- Events ----

        [Fact]
        public async Task Events_ShouldBeReadable_WithoutAToken()
        {
            var (eventId, _) = await _fixture.CreateEventAsync(seats: 3);

            var detail = await GetJsonAsync(Anonymous(), $"/api/Events/{eventId}");
            var list = await GetJsonAsync(Anonymous(), "/api/Events?pageSize=100");

            Assert.Equal(eventId, detail.GetProperty("id").GetInt32());
            Assert.Equal("Test Event", detail.GetProperty("name").GetString());
            Assert.Equal(3, detail.GetProperty("seatCount").GetInt32());
            Assert.True(detail.TryGetProperty("date", out _));
            Assert.True(list.GetProperty("totalCount").GetInt32() >= 1);
            Assert.Equal(1, list.GetProperty("page").GetInt32());
            Assert.Equal(100, list.GetProperty("pageSize").GetInt32());
        }

        [Theory]
        [InlineData("/api/Events/2147483647")]
        [InlineData("/api/Events/2147483647/seats")]
        public async Task UnknownEvent_ShouldReturn404(string url)
        {
            var response = await Anonymous().GetAsync(url);

            await ProblemAssert.HasCodeAsync(response, HttpStatusCode.NotFound, "event_not_found");
        }

        // ---- Seat status ----

        [Fact]
        public async Task SeatStatus_ShouldFollowTheBooking()
        {
            var (eventId, seatIds) = await _fixture.CreateEventAsync(seats: 1);
            var seatId = seatIds[0];
            var client = await _fixture.Api.CreateUserClientAsync();

            Assert.Equal("available", (await ReadSeatAsync(eventId, seatId)).GetProperty("status").GetString());

            var hold = await HoldAsync(client, seatId);
            var bookingId = hold.GetProperty("bookingId").GetInt32();
            var held = await ReadSeatAsync(eventId, seatId);
            Assert.Equal("held", held.GetProperty("status").GetString());
            Assert.Equal(hold.GetProperty("expiresAt").GetDateTime(), held.GetProperty("heldUntil").GetDateTime());

            (await client.PostAsync($"/api/Booking/{bookingId}/release", null)).EnsureSuccessStatusCode();
            var released = await ReadSeatAsync(eventId, seatId);
            Assert.Equal("available", released.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, released.GetProperty("heldUntil").ValueKind);
        }

        [Fact]
        public async Task SeatStatus_ShouldStayHeld_WhilePaymentIsPending_UntilTheGracePeriodEnds()
        {
            var (eventId, seatIds) = await _fixture.CreateEventAsync(seats: 1);
            var client = await _fixture.Api.CreateUserClientAsync();
            var hold = await HoldAsync(client, seatIds[0]);
            var bookingId = hold.GetProperty("bookingId").GetInt32();

            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/Booking/{bookingId}/confirm", null)).StatusCode);

            var seat = await ReadSeatAsync(eventId, seatIds[0]);
            Assert.Equal("held", seat.GetProperty("status").GetString());
            Assert.Equal(
                hold.GetProperty("expiresAt").GetDateTime().AddSeconds(new HoldOptions().PaymentGraceSeconds),
                seat.GetProperty("heldUntil").GetDateTime());
        }

        [Fact]
        public async Task SeatStatus_ShouldBeBooked_AfterConfirmation()
        {
            var (eventId, seatIds) = await _fixture.CreateEventAsync(seats: 1);
            var client = await _fixture.Api.CreateUserClientAsync();
            var bookingId = await HoldIdAsync(client, seatIds[0]);

            await _fixture.ConfirmThroughPaymentAsync(client, bookingId);

            var seat = await ReadSeatAsync(eventId, seatIds[0]);
            Assert.Equal("booked", seat.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, seat.GetProperty("heldUntil").ValueKind);
        }

        [Fact]
        public async Task SeatStatus_ShouldBeAvailable_WhenTheHoldRanOut_EvenBeforeTheSweeperMarksIt()
        {
            var (eventId, seatIds) = await _fixture.CreateEventAsync(seats: 1);
            var userId = await _fixture.CreateUserAsync();
            var now = _fixture.Api.Clock.GetUtcNow().UtcDateTime;
            await using (var db = _fixture.CreateContext())
            {
                db.Bookings.Add(new Booking
                {
                    SeatId = seatIds[0],
                    UserId = userId,
                    Status = BookingStatus.Held,
                    CreatedAt = now.AddMinutes(-6),
                    ExpiresAt = now.AddMinutes(-1)
                });
                await db.SaveChangesAsync();
            }

            var seat = await ReadSeatAsync(eventId, seatIds[0]);

            Assert.Equal("available", seat.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, seat.GetProperty("heldUntil").ValueKind);
        }

        [Fact]
        public async Task AnonymousSeatResponse_ShouldNotSayWhoHoldsTheSeat()
        {
            var (eventId, seatIds) = await _fixture.CreateEventAsync(seats: 3);
            var client = await _fixture.Api.CreateUserClientAsync();
            await HoldIdAsync(client, seatIds[1]);
            var booked = await HoldIdAsync(client, seatIds[2]);
            await _fixture.ConfirmThroughPaymentAsync(client, booked);

            var response = await Anonymous().GetAsync($"/api/Events/{eventId}/seats");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var raw = await response.Content.ReadAsStringAsync();
            var page = JsonDocument.Parse(raw).RootElement;

            // Free, held and booked: exactly these fields, whatever the state.
            var expected = new[] { "heldUntil", "row", "seatId", "seatNumber", "section", "status" };
            var items = page.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(new[] { "available", "held", "booked" }, items.Select(s => s.GetProperty("status").GetString()));
            foreach (var seat in items)
            {
                Assert.Equal(expected, PropertyNames(seat));
            }
            Assert.DoesNotContain("userId", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("bookingId", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("email", raw, StringComparison.OrdinalIgnoreCase);
        }

        // ---- Paging ----

        [Theory]
        [InlineData("/api/Events?page=0")]
        [InlineData("/api/Events?pageSize=0")]
        [InlineData("/api/Events?pageSize=101")]
        [InlineData("/api/Events?page=abc")]
        [InlineData("/api/Events/1/seats?page=0")]
        [InlineData("/api/Events/1/seats?pageSize=501")]
        public async Task AnonymousPaging_ShouldRejectValuesOutsideTheLimits(string url)
        {
            var response = await Anonymous().GetAsync(url);

            await ProblemAssert.HasCodeAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        }

        [Theory]
        [InlineData("/api/Booking?page=0")]
        [InlineData("/api/Booking?pageSize=0")]
        [InlineData("/api/Booking?pageSize=101")]
        public async Task BookingPaging_ShouldRejectValuesOutsideTheLimits(string url)
        {
            var client = await _fixture.Api.CreateUserClientAsync();

            var response = await client.GetAsync(url);

            await ProblemAssert.HasCodeAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        }

        [Fact]
        public async Task SeatPaging_ShouldReturnStablePages_AndTheTotal()
        {
            var (eventId, seatIds) = await _fixture.CreateEventAsync(seats: 5);
            var client = Anonymous();

            var first = await GetJsonAsync(client, $"/api/Events/{eventId}/seats?page=1&pageSize=2");
            var last = await GetJsonAsync(client, $"/api/Events/{eventId}/seats?page=3&pageSize=2");
            var beyond = await GetJsonAsync(client, $"/api/Events/{eventId}/seats?page=4&pageSize=2");
            var largest = await GetJsonAsync(client, $"/api/Events/{eventId}/seats?pageSize=500");

            static int[] Ids(JsonElement page) =>
                page.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("seatId").GetInt32()).ToArray();

            Assert.Equal(seatIds.Take(2), Ids(first));
            Assert.Equal(seatIds.Skip(4), Ids(last));
            Assert.Empty(Ids(beyond));
            Assert.Equal(seatIds, Ids(largest));
            foreach (var page in new[] { first, last, beyond, largest })
            {
                Assert.Equal(5, page.GetProperty("totalCount").GetInt32());
            }
            Assert.Equal(3, last.GetProperty("page").GetInt32());
            Assert.Equal(2, last.GetProperty("pageSize").GetInt32());
        }

        [Fact]
        public async Task SeatPaging_ShouldDefaultToOnePageForTheSeedEvent()
        {
            var (eventId, _) = await _fixture.CreateEventAsync(seats: 100);

            var page = await GetJsonAsync(Anonymous(), $"/api/Events/{eventId}/seats");

            Assert.Equal(100, page.GetProperty("items").GetArrayLength());
        }

        // ---- Bookings ----

        [Theory]
        [InlineData("/api/Booking")]
        [InlineData("/api/Booking/1")]
        public async Task BookingRead_ShouldReturn401_WithoutAToken(string url)
        {
            var response = await Anonymous().GetAsync(url);

            await ProblemAssert.HasCodeAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
        }

        [Theory]
        [InlineData("/api/Booking")]
        [InlineData("/api/Booking/1")]
        public async Task BookingRead_ShouldReturn401_WhenUserIdClaimIsNotANumber(string url)
        {
            var client = ApiFactory.Authorize(Anonymous(), AuthorizationTests.CreateToken(sub: "not-a-number"));

            var response = await client.GetAsync(url);

            await ProblemAssert.HasCodeAsync(response, HttpStatusCode.Unauthorized, "invalid_token");
        }

        [Fact]
        public async Task AdminEndpoint_ShouldReturn403ProblemBody_ForNormalUser()
        {
            var client = await _fixture.Api.CreateUserClientAsync();

            var response = await client.PostAsync("/api/Simulation/simulate-concurrency", null);

            await ProblemAssert.HasCodeAsync(response, HttpStatusCode.Forbidden, "forbidden");
        }

        [Fact]
        public async Task MyBookings_ShouldReturnOnlyMine_NewestFirst()
        {
            var (eventId, seatIds) = await _fixture.CreateEventAsync(seats: 3);
            var mine = await _fixture.Api.CreateUserClientAsync();
            var other = await _fixture.Api.CreateUserClientAsync();
            var first = await HoldIdAsync(mine, seatIds[0]);
            var second = await HoldIdAsync(mine, seatIds[1]);
            await HoldIdAsync(other, seatIds[2]);

            var page = await GetJsonAsync(mine, "/api/Booking");
            var firstPage = await GetJsonAsync(mine, "/api/Booking?page=1&pageSize=1");
            var secondPage = await GetJsonAsync(mine, "/api/Booking?page=2&pageSize=1");

            // The clock stands still, so both were created at the same moment: the newer id comes first.
            var items = page.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(new[] { second, first }, items.Select(b => b.GetProperty("bookingId").GetInt32()));
            Assert.Equal(2, page.GetProperty("totalCount").GetInt32());
            Assert.Equal(second, firstPage.GetProperty("items")[0].GetProperty("bookingId").GetInt32());
            Assert.Equal(first, secondPage.GetProperty("items")[0].GetProperty("bookingId").GetInt32());

            var item = items[1];
            Assert.Equal("held", item.GetProperty("status").GetString());
            Assert.Equal(seatIds[0], item.GetProperty("seat").GetProperty("seatId").GetInt32());
            Assert.Equal("A", item.GetProperty("seat").GetProperty("section").GetString());
            Assert.Equal(eventId, item.GetProperty("event").GetProperty("id").GetInt32());
            Assert.Equal("Test Event", item.GetProperty("event").GetProperty("name").GetString());
            Assert.False(item.TryGetProperty("userId", out _));
        }

        [Fact]
        public async Task BookingDetail_ShouldBeReadable_ByItsOwner()
        {
            var (_, seatIds) = await _fixture.CreateEventAsync(seats: 1);
            var client = await _fixture.Api.CreateUserClientAsync();
            var hold = await HoldAsync(client, seatIds[0]);
            var bookingId = hold.GetProperty("bookingId").GetInt32();

            var detail = await GetJsonAsync(client, $"/api/Booking/{bookingId}");

            Assert.Equal(bookingId, detail.GetProperty("bookingId").GetInt32());
            Assert.Equal("held", detail.GetProperty("status").GetString());
            Assert.Equal(hold.GetProperty("expiresAt").GetDateTime(), detail.GetProperty("expiresAt").GetDateTime());
            Assert.Equal(JsonValueKind.Null, detail.GetProperty("confirmedAt").ValueKind);
            Assert.True(detail.TryGetProperty("createdAt", out _));
        }

        [Fact]
        public async Task BookingDetail_ShouldReturn403_ForAnotherUser_AndForAdmin()
        {
            var (_, seatIds) = await _fixture.CreateEventAsync(seats: 1);
            var owner = await _fixture.Api.CreateUserClientAsync();
            var bookingId = await HoldIdAsync(owner, seatIds[0]);
            var other = await _fixture.Api.CreateUserClientAsync();
            var admin = await _fixture.Api.CreateAdminClientAsync();

            await ProblemAssert.HasCodeAsync(await other.GetAsync($"/api/Booking/{bookingId}"), HttpStatusCode.Forbidden, "not_hold_owner");
            await ProblemAssert.HasCodeAsync(await admin.GetAsync($"/api/Booking/{bookingId}"), HttpStatusCode.Forbidden, "not_hold_owner");
        }

        [Fact]
        public async Task BookingDetail_ShouldReturn404_WhenItDoesNotExist()
        {
            var client = await _fixture.Api.CreateUserClientAsync();

            var response = await client.GetAsync("/api/Booking/2147483647");

            await ProblemAssert.HasCodeAsync(response, HttpStatusCode.NotFound, "booking_not_found");
        }

        // ---- JSON contract ----

        [Fact]
        public async Task StatusValues_ShouldBeCamelCase_InEveryBookingResponse()
        {
            var (_, seatIds) = await _fixture.CreateEventAsync(seats: 2);
            var client = await _fixture.Api.CreateUserClientAsync();

            var hold = await HoldAsync(client, seatIds[0]);
            var bookingId = hold.GetProperty("bookingId").GetInt32();
            Assert.Equal("held", hold.GetProperty("status").GetString());

            var confirm = await (await client.PostAsync($"/api/Booking/{bookingId}/confirm", null)).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("paymentPending", confirm.GetProperty("status").GetString());
            Assert.Equal("paymentPending", (await GetJsonAsync(client, $"/api/Booking/{bookingId}")).GetProperty("status").GetString());

            var releasedId = await HoldIdAsync(client, seatIds[1]);
            var release = await (await client.PostAsync($"/api/Booking/{releasedId}/release", null)).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("released", release.GetProperty("status").GetString());
        }

        // ---- Settings and limits ----

        [Fact]
        public void Sweeper_ShouldRunEveryFiveSeconds_ByDefault()
        {
            Assert.Equal(5, new HoldOptions().SweepIntervalSeconds);

            var configuration = _fixture.Api.Services.GetRequiredService<IConfiguration>();
            Assert.Equal(5, configuration.GetValue<int>("Holds:SweepIntervalSeconds"));
        }

        [Fact]
        public async Task Reads_ShouldReturn429_WhenTheirLimitIsExceeded()
        {
            await using var api = new ApiFactory(_fixture, new Dictionary<string, string?> { ["RateLimiting__Read__PermitLimit"] = "3" });
            var client = api.CreateClient();

            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Events")).StatusCode);
            }

            var rejected = await client.GetAsync("/api/Events");

            await ProblemAssert.HasCodeAsync(rejected, HttpStatusCode.TooManyRequests, "rate_limited");
        }

        [Fact]
        public async Task Reads_ShouldNotUseUpTheBookingLimit()
        {
            await using var api = new ApiFactory(_fixture, new Dictionary<string, string?> { ["RateLimiting__Booking__PermitLimit"] = "3" });
            var client = await api.CreateUserClientAsync();
            var seatId = await _fixture.CreateFreeSeatAsync();

            for (var i = 0; i < 5; i++)
            {
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Booking")).StatusCode);
            }

            var hold = await client.PostAsJsonAsync("/api/Booking/hold", new { seatId });

            Assert.Equal(HttpStatusCode.OK, hold.StatusCode);
        }
    }
}
