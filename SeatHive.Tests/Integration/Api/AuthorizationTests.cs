using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace SeatHive.Tests.Integration.Api
{
    [Collection(TestCollections.ApiRead)]
    [Trait(TestCategories.Trait, TestCategories.Api)]
    public class AuthorizationTests
    {
        private const string ResetUrl = "/api/Setup/create-data";

        private readonly ContainersFixture _fixture;

        public AuthorizationTests(ContainersFixture fixture)
        {
            _fixture = fixture;
        }

        // The race simulation is open to every signed-in user; see SimulationTests.
        [Theory]
        [InlineData(ResetUrl)]
        public async Task AdminEndpoint_ShouldReturn401_ForAnonymous(string url)
        {
            var client = _fixture.Api.CreateClient();

            var response = await client.PostAsync(url, null, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Theory]
        [InlineData(ResetUrl)]
        public async Task AdminEndpoint_ShouldReturn403_ForNormalUser(string url)
        {
            var client = await _fixture.Api.SignInAsUserAsync();

            var response = await client.PostAsync(url, null, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        [Theory]
        [InlineData(ResetUrl)]
        public async Task AdminEndpoint_ShouldReturn200_ForAdmin(string url)
        {
            var client = await _fixture.Api.SignInAsAdminAsync();

            var response = await client.PostAsync(url, null, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task Reset_ShouldKeepUsers_AndRestartSeatIds()
        {
            var ct = TestContext.Current.CancellationToken;
            var email = ApiFactory.UniqueEmail();
            var anonymous = _fixture.Api.CreateClient();
            (await ApiFactory.RegisterAsync(anonymous, email, "Passw0rd!")).EnsureSuccessStatusCode();
            var admin = await _fixture.Api.CreateAdminClientAsync();

            // Twice, so the second run proves ids restart instead of continuing.
            (await admin.PostAsync(ResetUrl, null, ct)).EnsureSuccessStatusCode();
            (await admin.PostAsync(ResetUrl, null, ct)).EnsureSuccessStatusCode();

            // The user registered before the reset can still log in.
            await ApiFactory.LoginAsync(anonymous, email, "Passw0rd!");

            await using var db = _fixture.CreateContext();
            Assert.Equal(100, await db.Seats.CountAsync(cancellationToken: ct));
            Assert.Equal(1, await db.Seats.MinAsync(s => s.Id, cancellationToken: ct));
            Assert.Equal(1, await db.Events.CountAsync(cancellationToken: ct));
        }

        [Fact]
        public async Task Reset_ShouldSeedAHall_OfTenRowsOfTenSeats_InThreeBlocks()
        {
            var ct = TestContext.Current.CancellationToken;
            var admin = await _fixture.Api.CreateAdminClientAsync();

            (await admin.PostAsync(ResetUrl, null, ct)).EnsureSuccessStatusCode();

            await using var db = _fixture.CreateContext();
            var seats = await db.Seats.AsNoTracking().OrderBy(s => s.Id).ToListAsync(ct);

            // Rows A to J, each with the seats 1 to 10: 1-3 on the left, 4-7 in the centre, 8-10 on the right.
            var rows = seats.GroupBy(s => s.Row).OrderBy(g => g.Key).ToList();
            Assert.Equal(new[] { "A", "B", "C", "D", "E", "F", "G", "H", "I", "J" }, rows.Select(g => g.Key));
            Assert.All(rows, row =>
            {
                Assert.Equal(Enumerable.Range(1, 10), row.Select(s => s.SeatNumber).Order());
                Assert.All(row, s => Assert.Equal(s.SeatNumber <= 3 ? "Left" : s.SeatNumber <= 7 ? "Centre" : "Right", s.Section));
            });

            // The lowest id is the first free seat a race takes: the centre of the front row.
            Assert.Equal(("A", 4, "Centre"), (seats[0].Row, seats[0].SeatNumber, seats[0].Section));
            Assert.Equal(new[] { 4, 5, 6, 7, 1, 2, 3, 8, 9, 10 }, seats.Take(10).Select(s => s.SeatNumber));
        }

        [Fact]
        public async Task Token_ShouldCarryRoleClaim()
        {
            var client = _fixture.Api.CreateClient();
            var email = ApiFactory.UniqueEmail();
            (await ApiFactory.RegisterAsync(client, email, "Passw0rd!")).EnsureSuccessStatusCode();

            var userToken = await ApiFactory.LoginAsync(client, email, "Passw0rd!");
            var adminToken = await ApiFactory.LoginAsync(client, ApiFactory.AdminEmail, ApiFactory.AdminPassword);

            Assert.Equal("User", ReadClaim(userToken, "role"));
            Assert.Equal("Admin", ReadClaim(adminToken, "role"));
        }

        [Fact]
        public async Task Booking_ShouldReturn401_WhenUserIdClaimIsNotANumber()
        {
            var client = ApiFactory.Authorize(_fixture.Api.CreateClient(), CreateToken(sub: "not-a-number"));

            var response = await client.PostAsJsonAsync("/api/Booking/hold", new { seatId = 1 }, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        private static string? ReadClaim(string token, string type)
        {
            return new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.FirstOrDefault(c => c.Type == type)?.Value;
        }

        // A correctly signed token, so only the content of "sub" is under test.
        internal static string CreateToken(string sub)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ApiFactory.JwtKey));
            var token = new JwtSecurityToken(
                issuer: "SeatHive.Api",
                audience: "SeatHive.Client",
                claims: new[] { new Claim("sub", sub), new Claim("role", "User") },
                expires: DateTime.UtcNow.AddMinutes(5),
                signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
