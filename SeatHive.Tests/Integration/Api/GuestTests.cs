using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SeatHive.Api.Models;
using SeatHive.Api.Services;

namespace SeatHive.Tests.Integration.Api
{
    [Collection(TestCollections.ApiRead)]
    [Trait(TestCategories.Trait, TestCategories.Api)]
    public class GuestTests
    {
        private readonly ContainersFixture _fixture;

        public GuestTests(ContainersFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task Guest_ShouldGetAToken_ThatWorksLikeAUsers()
        {
            var ct = TestContext.Current.CancellationToken;
            var client = _fixture.Api.CreateClient();

            var token = await CreateGuestAsync(client);

            Assert.Equal(Roles.Guest, ReadClaim(token, "role"));

            var bookings = await ApiFactory.Authorize(client, token).GetAsync("/api/Booking", ct);
            Assert.Equal(HttpStatusCode.OK, bookings.StatusCode);
        }

        [Fact]
        public async Task Guest_ShouldBeAUserOfItsOwn_EveryTime()
        {
            var ct = TestContext.Current.CancellationToken;
            var client = _fixture.Api.CreateClient();

            var first = int.Parse(ReadClaim(await CreateGuestAsync(client), "sub")!);
            var second = int.Parse(ReadClaim(await CreateGuestAsync(client), "sub")!);

            Assert.NotEqual(first, second);

            await using var db = _fixture.CreateContext();
            var guests = await db.Users.AsNoTracking().Where(u => u.Id == first || u.Id == second).ToListAsync(ct);
            Assert.Equal(2, guests.Count);
            Assert.All(guests, g => Assert.Equal(Roles.Guest, g.Role));
            Assert.All(guests, g => Assert.True(GuestAccounts.IsGuestEmail(g.Email)));
        }

        [Fact]
        public async Task Register_ShouldReturn400_ForAnEmailAtTheGuestDomain()
        {
            var client = _fixture.Api.CreateClient();

            var response = await ApiFactory.RegisterAsync(client, $"someone@{GuestAccounts.Domain}", "Passw0rd!");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        internal static async Task<string> CreateGuestAsync(HttpClient client)
        {
            var response = await client.PostAsync("/api/Auth/guest", null);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return body.GetProperty("token").GetString()!;
        }

        internal static string? ReadClaim(string token, string type)
        {
            return new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.FirstOrDefault(c => c.Type == type)?.Value;
        }
    }
}
