using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SeatHive.Tests.Integration.Api
{
    [Collection(ContainersCollection.Name)]
    public class BookingApiTests
    {
        private readonly ContainersFixture _fixture;

        public BookingApiTests(ContainersFixture fixture)
        {
            _fixture = fixture;
        }

        // Every error body is ProblemDetails with a machine-readable "code".
        private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
        {
            Assert.Equal(status, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.TryGetProperty("code", out var actualCode), "The problem body has no \"code\".");
            Assert.Equal(code, actualCode.GetString());
        }

        private async Task<int> CountBookingsAsync(int seatId, string status)
        {
            await using var db = _fixture.CreateContext();
            return await db.Database
                .SqlQueryRaw<int>(
                    "SELECT COUNT(*)::int AS \"Value\" FROM \"Bookings\" WHERE \"SeatId\" = {0} AND \"Status\" = {1}",
                    seatId, status)
                .SingleAsync();
        }

        [Fact]
        public async Task Booking_ShouldReturn200_WithTheConfirmedBooking()
        {
            var client = await _fixture.Api.CreateUserClientAsync();
            var seatId = await _fixture.CreateFreeSeatAsync();

            var response = await client.PostAsJsonAsync("/api/Booking", new { seatId });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("bookingId").GetInt32() > 0);
            Assert.Equal(seatId, body.GetProperty("seatId").GetInt32());
            Assert.Equal("Confirmed", body.GetProperty("status").GetString());
            Assert.Equal(1, await CountBookingsAsync(seatId, "Confirmed"));
        }

        [Fact]
        public async Task Booking_ShouldReturn409_WhenSeatIsAlreadyBooked()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var first = await _fixture.Api.CreateUserClientAsync();
            var second = await _fixture.Api.CreateUserClientAsync();
            (await first.PostAsJsonAsync("/api/Booking", new { seatId })).EnsureSuccessStatusCode();

            var response = await second.PostAsJsonAsync("/api/Booking", new { seatId });

            await AssertProblemAsync(response, HttpStatusCode.Conflict, "seat_already_booked");
        }

        [Fact]
        public async Task Booking_ShouldReturn404_WhenSeatDoesNotExist()
        {
            var client = await _fixture.Api.CreateUserClientAsync();

            var response = await client.PostAsJsonAsync("/api/Booking", new { seatId = int.MaxValue });

            await AssertProblemAsync(response, HttpStatusCode.NotFound, "seat_not_found");
        }

        [Fact]
        public async Task Booking_ShouldReturn409WithItsOwnCode_WhenSeatLockIsHeldBySomeoneElse()
        {
            var client = await _fixture.Api.CreateUserClientAsync();
            var seatId = await _fixture.CreateFreeSeatAsync();
            var redis = _fixture.Redis.GetDatabase();
            var lockKey = $"lock:seat:{seatId}";
            await redis.StringSetAsync(lockKey, "held-by-another-request", TimeSpan.FromSeconds(30));

            try
            {
                var response = await client.PostAsJsonAsync("/api/Booking", new { seatId });

                await AssertProblemAsync(response, HttpStatusCode.Conflict, "seat_locked");
            }
            finally
            {
                await redis.KeyDeleteAsync(lockKey);
            }
        }

        [Fact]
        public async Task Booking_ShouldSucceed_WhenSeatOnlyHasExpiredAndReleasedBookings()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var formerUsers = await _fixture.CreateUsersAsync(2);
            await using (var db = _fixture.CreateContext())
            {
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO \"Bookings\" (\"SeatId\", \"UserId\", \"Status\", \"CreatedAt\") VALUES ({0}, {1}, 'Expired', now()), ({0}, {2}, 'Released', now())",
                    seatId, formerUsers[0], formerUsers[1]);
            }
            var client = await _fixture.Api.CreateUserClientAsync();

            var response = await client.PostAsJsonAsync("/api/Booking", new { seatId });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, await CountBookingsAsync(seatId, "Confirmed"));
        }

        [Fact]
        public async Task Booking_ShouldReturn400WithCode_ForInvalidBody()
        {
            var client = await _fixture.Api.CreateUserClientAsync();

            var response = await client.PostAsJsonAsync("/api/Booking", new { seatId = 0 });

            await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        }

        [Fact]
        public async Task Booking_ShouldReturn401WithCode_WhenUserIdClaimIsNotANumber()
        {
            var client = ApiFactory.Authorize(_fixture.Api.CreateClient(), AuthorizationTests.CreateToken(sub: "not-a-number"));

            var response = await client.PostAsJsonAsync("/api/Booking", new { seatId = 1 });

            await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "invalid_token");
        }

        [Fact]
        public async Task Register_ShouldReturn409WithCode_ForDuplicateEmail()
        {
            var client = _fixture.Api.CreateClient();
            var email = ApiFactory.UniqueEmail();
            (await ApiFactory.RegisterAsync(client, email, "Passw0rd!")).EnsureSuccessStatusCode();

            var response = await ApiFactory.RegisterAsync(client, email, "Passw0rd!");

            await AssertProblemAsync(response, HttpStatusCode.Conflict, "email_already_registered");
        }

        [Fact]
        public async Task Register_ShouldReturn400WithCode_ForInvalidBody()
        {
            var client = _fixture.Api.CreateClient();

            var response = await ApiFactory.RegisterAsync(client, "not-an-email", "short");

            await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        }

        [Fact]
        public async Task Login_ShouldReturn401WithCode_ForWrongPassword()
        {
            var client = _fixture.Api.CreateClient();
            var email = ApiFactory.UniqueEmail();
            (await ApiFactory.RegisterAsync(client, email, "Passw0rd!")).EnsureSuccessStatusCode();

            var response = await client.PostAsJsonAsync("/api/Auth/login", new { email, password = "WrongPassw0rd!" });

            await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "invalid_credentials");
        }
    }
}
