using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SeatHive.Api.Models;

namespace SeatHive.Tests.Integration.Api
{
    [Collection(TestCollections.ApiBooking)]
    [Trait(TestCategories.Trait, TestCategories.Api)]
    public class BookingApiTests
    {
        private const string HoldUrl = "/api/Booking/hold";

        private readonly ContainersFixture _fixture;

        public BookingApiTests(ContainersFixture fixture)
        {
            _fixture = fixture;
        }

        private static string ConfirmUrl(int bookingId) => $"/api/Booking/{bookingId}/confirm";
        private static string ReleaseUrl(int bookingId) => $"/api/Booking/{bookingId}/release";

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

        private async Task<(HttpClient Client, int UserId)> CreateUserAsync()
        {
            var email = ApiFactory.UniqueEmail();
            var client = await _fixture.Api.CreateUserClientAsync(email);

            await using var db = _fixture.CreateContext();
            return (client, await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync());
        }

        // Holds a seat and returns the booking id.
        private static async Task<int> HoldAsync(HttpClient client, int seatId)
        {
            var response = await client.PostAsJsonAsync(HoldUrl, new { seatId });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return body.GetProperty("bookingId").GetInt32();
        }

        // A hold whose time ran out but which nobody has marked as expired yet.
        private async Task<int> InsertExpiredHoldAsync(int seatId, int userId)
        {
            var now = _fixture.Api.Clock.GetUtcNow().UtcDateTime;

            var booking = new Booking
            {
                SeatId = seatId,
                UserId = userId,
                Status = BookingStatus.Held,
                CreatedAt = now.AddMinutes(-6),
                ExpiresAt = now.AddMinutes(-1)
            };

            await using var db = _fixture.CreateContext();
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();
            return booking.Id;
        }

        [Fact]
        public async Task Hold_ShouldReturn200_WithBookingIdAndExpiresAt()
        {
            var client = await _fixture.Api.CreateUserClientAsync();
            var seatId = await _fixture.CreateFreeSeatAsync();

            var response = await client.PostAsJsonAsync(HoldUrl, new { seatId });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("bookingId").GetInt32() > 0);
            Assert.Equal(seatId, body.GetProperty("seatId").GetInt32());
            Assert.Equal("held", body.GetProperty("status").GetString());
            // Default hold duration is 5 minutes.
            Assert.Equal(_fixture.Api.Clock.GetUtcNow().AddMinutes(5), body.GetProperty("expiresAt").GetDateTimeOffset());
            Assert.Equal(1, await CountBookingsAsync(seatId, "Held"));
        }

        [Fact]
        public async Task Hold_ShouldReturnTheSameHold_WhenRepeatedByTheSameUser()
        {
            var client = await _fixture.Api.CreateUserClientAsync();
            var seatId = await _fixture.CreateFreeSeatAsync();

            var first = await (await client.PostAsJsonAsync(HoldUrl, new { seatId })).Content.ReadFromJsonAsync<JsonElement>();
            var secondResponse = await client.PostAsJsonAsync(HoldUrl, new { seatId });

            Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
            var second = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(first.GetProperty("bookingId").GetInt32(), second.GetProperty("bookingId").GetInt32());
            Assert.Equal(first.GetProperty("expiresAt").GetDateTimeOffset(), second.GetProperty("expiresAt").GetDateTimeOffset());
            Assert.Equal(1, await CountBookingsAsync(seatId, "Held"));
        }

        [Fact]
        public async Task Hold_ShouldReturn409_WhenSeatIsHeldByAnotherUser()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var first = await _fixture.Api.CreateUserClientAsync();
            var second = await _fixture.Api.CreateUserClientAsync();
            await HoldAsync(first, seatId);

            var response = await second.PostAsJsonAsync(HoldUrl, new { seatId });

            await AssertProblemAsync(response, HttpStatusCode.Conflict, "seat_held");
        }

        [Fact]
        public async Task Hold_ShouldReturn409_WhenSeatIsAlreadyBooked()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var first = await _fixture.Api.CreateUserClientAsync();
            var second = await _fixture.Api.CreateUserClientAsync();
            var bookingId = await HoldAsync(first, seatId);
            await _fixture.ConfirmThroughPaymentAsync(first, bookingId);

            var response = await second.PostAsJsonAsync(HoldUrl, new { seatId });

            await AssertProblemAsync(response, HttpStatusCode.Conflict, "seat_already_booked");
        }

        [Fact]
        public async Task Hold_ShouldReturn404_WhenSeatDoesNotExist()
        {
            var client = await _fixture.Api.CreateUserClientAsync();

            var response = await client.PostAsJsonAsync(HoldUrl, new { seatId = int.MaxValue });

            await AssertProblemAsync(response, HttpStatusCode.NotFound, "seat_not_found");
        }

        [Fact]
        public async Task Hold_ShouldReturn409WithItsOwnCode_WhenSeatLockIsHeldBySomeoneElse()
        {
            var client = await _fixture.Api.CreateUserClientAsync();
            var seatId = await _fixture.CreateFreeSeatAsync();
            var redis = _fixture.Redis.GetDatabase();
            var lockKey = $"lock:seat:{seatId}";
            await redis.StringSetAsync(lockKey, "held-by-another-request", TimeSpan.FromSeconds(30));

            try
            {
                var response = await client.PostAsJsonAsync(HoldUrl, new { seatId });

                await AssertProblemAsync(response, HttpStatusCode.Conflict, "seat_locked");
            }
            finally
            {
                await redis.KeyDeleteAsync(lockKey);
            }
        }

        [Fact]
        public async Task Hold_ShouldSucceed_WhenSeatOnlyHasExpiredAndReleasedBookings()
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

            var response = await client.PostAsJsonAsync(HoldUrl, new { seatId });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, await CountBookingsAsync(seatId, "Held"));
        }

        [Fact]
        public async Task Hold_ShouldSucceed_WhenTheOnlyHoldOnTheSeatHasRunOut()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var formerUserId = await _fixture.CreateUserAsync();
            await InsertExpiredHoldAsync(seatId, formerUserId);
            var client = await _fixture.Api.CreateUserClientAsync();

            var response = await client.PostAsJsonAsync(HoldUrl, new { seatId });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, await CountBookingsAsync(seatId, "Held"));
            Assert.Equal(1, await CountBookingsAsync(seatId, "Expired"));
        }

        [Fact]
        public async Task Hold_ShouldReturn409_WhenUserIsAtTheHoldLimit()
        {
            var client = await _fixture.Api.CreateUserClientAsync();
            // Default limit is 4.
            for (var i = 0; i < 4; i++)
            {
                await HoldAsync(client, await _fixture.CreateFreeSeatAsync());
            }
            var seatId = await _fixture.CreateFreeSeatAsync();

            var response = await client.PostAsJsonAsync(HoldUrl, new { seatId });

            await AssertProblemAsync(response, HttpStatusCode.Conflict, "hold_limit_reached");
            Assert.Equal(0, await CountBookingsAsync(seatId, "Held"));
        }

        [Fact]
        public async Task Hold_ShouldReturn400WithCode_ForInvalidBody()
        {
            var client = await _fixture.Api.CreateUserClientAsync();

            var response = await client.PostAsJsonAsync(HoldUrl, new { seatId = 0 });

            await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        }

        [Fact]
        public async Task Hold_ShouldReturn401WithCode_WhenUserIdClaimIsNotANumber()
        {
            var client = ApiFactory.Authorize(_fixture.Api.CreateClient(), AuthorizationTests.CreateToken(sub: "not-a-number"));

            var response = await client.PostAsJsonAsync(HoldUrl, new { seatId = 1 });

            await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "invalid_token");
        }

        [Fact]
        public async Task DirectBookingEndpoint_ShouldBeGone()
        {
            var client = await _fixture.Api.CreateUserClientAsync();
            var seatId = await _fixture.CreateFreeSeatAsync();

            var response = await client.PostAsJsonAsync("/api/Booking", new { seatId });

            Assert.True(
                response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"got {(int)response.StatusCode}");
            Assert.Equal(0, await CountBookingsAsync(seatId, "Confirmed"));
        }

        [Fact]
        public async Task Confirm_ShouldReturn202_AndStartThePayment()
        {
            var client = await _fixture.Api.CreateUserClientAsync();
            var seatId = await _fixture.CreateFreeSeatAsync();
            var bookingId = await HoldAsync(client, seatId);

            var response = await client.PostAsync(ConfirmUrl(bookingId), null);

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(bookingId, body.GetProperty("bookingId").GetInt32());
            Assert.Equal(seatId, body.GetProperty("seatId").GetInt32());
            Assert.Equal("paymentPending", body.GetProperty("status").GetString());
            Assert.Equal(1, await CountBookingsAsync(seatId, "PaymentPending"));
            Assert.Equal(0, await CountBookingsAsync(seatId, "Confirmed"));

            // Confirming again while the payment is in progress changes nothing.
            var again = await client.PostAsync(ConfirmUrl(bookingId), null);
            Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        }

        [Fact]
        public async Task Confirm_ShouldReturn200_WhenTheOwnerRepeatsItAfterTheBookingIsConfirmed()
        {
            var client = await _fixture.Api.CreateUserClientAsync();
            var seatId = await _fixture.CreateFreeSeatAsync();
            var bookingId = await HoldAsync(client, seatId);
            await _fixture.ConfirmThroughPaymentAsync(client, bookingId);

            var response = await client.PostAsync(ConfirmUrl(bookingId), null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("confirmed", body.GetProperty("status").GetString());
            Assert.Equal(1, await CountBookingsAsync(seatId, "Confirmed"));
        }

        [Fact]
        public async Task Confirm_ShouldReturn403_ForAnotherUsersHold()
        {
            var owner = await _fixture.Api.CreateUserClientAsync();
            var other = await _fixture.Api.CreateUserClientAsync();
            var seatId = await _fixture.CreateFreeSeatAsync();
            var bookingId = await HoldAsync(owner, seatId);

            var response = await other.PostAsync(ConfirmUrl(bookingId), null);

            await AssertProblemAsync(response, HttpStatusCode.Forbidden, "not_hold_owner");
            Assert.Equal(1, await CountBookingsAsync(seatId, "Held"));
        }

        [Fact]
        public async Task Confirm_ShouldReturn403_ForAnotherUsersConfirmedBooking()
        {
            var owner = await _fixture.Api.CreateUserClientAsync();
            var other = await _fixture.Api.CreateUserClientAsync();
            var bookingId = await HoldAsync(owner, await _fixture.CreateFreeSeatAsync());
            await _fixture.ConfirmThroughPaymentAsync(owner, bookingId);

            var response = await other.PostAsync(ConfirmUrl(bookingId), null);

            await AssertProblemAsync(response, HttpStatusCode.Forbidden, "not_hold_owner");
        }

        [Fact]
        public async Task Confirm_ShouldReturn410_WhenTheHoldHasExpired()
        {
            var (client, userId) = await CreateUserAsync();
            var seatId = await _fixture.CreateFreeSeatAsync();
            var bookingId = await InsertExpiredHoldAsync(seatId, userId);

            var response = await client.PostAsync(ConfirmUrl(bookingId), null);

            await AssertProblemAsync(response, HttpStatusCode.Gone, "hold_expired");
            Assert.Equal(0, await CountBookingsAsync(seatId, "Confirmed"));
        }

        [Fact]
        public async Task Confirm_ShouldReturn409_WhenTheHoldWasReleased()
        {
            var client = await _fixture.Api.CreateUserClientAsync();
            var bookingId = await HoldAsync(client, await _fixture.CreateFreeSeatAsync());
            (await client.PostAsync(ReleaseUrl(bookingId), null)).EnsureSuccessStatusCode();

            var response = await client.PostAsync(ConfirmUrl(bookingId), null);

            await AssertProblemAsync(response, HttpStatusCode.Conflict, "hold_not_active");
        }

        [Fact]
        public async Task ConfirmAndRelease_ShouldReturn404_ForUnknownBooking()
        {
            var client = await _fixture.Api.CreateUserClientAsync();

            await AssertProblemAsync(await client.PostAsync(ConfirmUrl(int.MaxValue), null), HttpStatusCode.NotFound, "booking_not_found");
            await AssertProblemAsync(await client.PostAsync(ReleaseUrl(int.MaxValue), null), HttpStatusCode.NotFound, "booking_not_found");
        }

        [Fact]
        public async Task Release_ShouldReturn200_AndFreeTheSeatForAnotherUser()
        {
            var owner = await _fixture.Api.CreateUserClientAsync();
            var other = await _fixture.Api.CreateUserClientAsync();
            var seatId = await _fixture.CreateFreeSeatAsync();
            var bookingId = await HoldAsync(owner, seatId);

            var response = await owner.PostAsync(ReleaseUrl(bookingId), null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(bookingId, body.GetProperty("bookingId").GetInt32());
            Assert.Equal("released", body.GetProperty("status").GetString());
            Assert.Equal(1, await CountBookingsAsync(seatId, "Released"));

            await HoldAsync(other, seatId);
        }

        [Fact]
        public async Task Release_ShouldReturn403_ForAnotherUsersHold()
        {
            var owner = await _fixture.Api.CreateUserClientAsync();
            var other = await _fixture.Api.CreateUserClientAsync();
            var seatId = await _fixture.CreateFreeSeatAsync();
            var bookingId = await HoldAsync(owner, seatId);

            var response = await other.PostAsync(ReleaseUrl(bookingId), null);

            await AssertProblemAsync(response, HttpStatusCode.Forbidden, "not_hold_owner");
            Assert.Equal(1, await CountBookingsAsync(seatId, "Held"));
        }

        [Fact]
        public async Task Release_ShouldReturn409_ForAConfirmedBooking()
        {
            var client = await _fixture.Api.CreateUserClientAsync();
            var seatId = await _fixture.CreateFreeSeatAsync();
            var bookingId = await HoldAsync(client, seatId);
            await _fixture.ConfirmThroughPaymentAsync(client, bookingId);

            var response = await client.PostAsync(ReleaseUrl(bookingId), null);

            await AssertProblemAsync(response, HttpStatusCode.Conflict, "hold_not_active");
            Assert.Equal(1, await CountBookingsAsync(seatId, "Confirmed"));
        }

        [Fact]
        public async Task Reset_ShouldNotReuseBookingIds()
        {
            var client = await _fixture.Api.CreateUserClientAsync();
            var admin = await _fixture.Api.CreateAdminClientAsync();
            var before = await HoldAsync(client, await _fixture.CreateFreeSeatAsync());

            (await admin.PostAsync("/api/Setup/create-data", null)).EnsureSuccessStatusCode();
            var after = await HoldAsync(client, await _fixture.CreateFreeSeatAsync());

            // A message about an old booking, still on its way, must never meet a new booking with the same id.
            Assert.True(after > before, $"The booking after the reset got id {after}; the one before it had {before}.");
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
