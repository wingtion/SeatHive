using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace SeatHive.Tests.Integration.Api
{
    [Collection(ContainersCollection.Name)]
    public class AuthorizationTests
    {
        private const string ResetUrl = "/api/Setup/create-data";
        private const string SimulationUrl = "/api/Simulation/simulate-concurrency";

        private readonly ContainersFixture _fixture;

        public AuthorizationTests(ContainersFixture fixture)
        {
            _fixture = fixture;
        }

        [Theory]
        [InlineData(ResetUrl)]
        [InlineData(SimulationUrl)]
        public async Task AdminEndpoint_ShouldReturn401_ForAnonymous(string url)
        {
            var client = _fixture.Api.CreateClient();

            var response = await client.PostAsync(url, null);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Theory]
        [InlineData(ResetUrl)]
        [InlineData(SimulationUrl)]
        public async Task AdminEndpoint_ShouldReturn403_ForNormalUser(string url)
        {
            var client = await _fixture.Api.CreateUserClientAsync();

            var response = await client.PostAsync(url, null);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        [Theory]
        [InlineData(ResetUrl)]
        [InlineData(SimulationUrl)]
        public async Task AdminEndpoint_ShouldReturn200_ForAdmin(string url)
        {
            var client = await _fixture.Api.CreateAdminClientAsync();

            var response = await client.PostAsync(url, null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task Reset_ShouldKeepUsers_AndRestartSeatIds()
        {
            var email = ApiFactory.UniqueEmail();
            var anonymous = _fixture.Api.CreateClient();
            (await ApiFactory.RegisterAsync(anonymous, email, "Passw0rd!")).EnsureSuccessStatusCode();
            var admin = await _fixture.Api.CreateAdminClientAsync();

            // Twice, so the second run proves ids restart instead of continuing.
            (await admin.PostAsync(ResetUrl, null)).EnsureSuccessStatusCode();
            (await admin.PostAsync(ResetUrl, null)).EnsureSuccessStatusCode();

            // The user registered before the reset can still log in.
            await ApiFactory.LoginAsync(anonymous, email, "Passw0rd!");

            await using var db = _fixture.CreateContext();
            Assert.Equal(100, await db.Seats.CountAsync());
            Assert.Equal(1, await db.Seats.MinAsync(s => s.Id));
            Assert.Equal(1, await db.Events.CountAsync());
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

            var response = await client.PostAsJsonAsync("/api/Booking/hold", new { seatId = 1 });

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
