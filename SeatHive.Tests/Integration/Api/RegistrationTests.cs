using System.Net;
using System.Net.Http.Json;

namespace SeatHive.Tests.Integration.Api
{
    [Collection(ContainersCollection.Name)]
    public class RegistrationTests
    {
        private readonly ContainersFixture _fixture;

        public RegistrationTests(ContainersFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task Register_ShouldRejectSameEmail_WithDifferentCasing()
        {
            var client = _fixture.Api.CreateClient();
            var email = ApiFactory.UniqueEmail();

            var first = await ApiFactory.RegisterAsync(client, email, "Passw0rd!");
            var second = await ApiFactory.RegisterAsync(client, email.ToUpperInvariant(), "Passw0rd!");

            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

            // Login is case-insensitive too.
            await ApiFactory.LoginAsync(client, email.ToUpperInvariant(), "Passw0rd!");
        }

        [Fact]
        public async Task Register_ShouldCreateExactlyOneUser_WhenSameEmailRaces()
        {
            var client = _fixture.Api.CreateClient();
            var email = ApiFactory.UniqueEmail();

            var responses = await Task.WhenAll(Enumerable.Range(0, 10)
                .Select(_ => ApiFactory.RegisterAsync(client, email, "Passw0rd!")));

            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
            Assert.Equal(9, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        }

        public static TheoryData<string, string, string> InvalidRegistrations => new()
        {
            { "empty email", "", "Passw0rd!" },
            { "malformed email", "not-an-email", "Passw0rd!" },
            { "empty password", "valid@seathive.test", "" },
            { "7 character password", "valid@seathive.test", "Passw0r" },
            { "73 byte password", "valid@seathive.test", new string('a', 73) },
            // 40 characters, but 80 bytes in UTF-8: BCrypt would silently ignore everything after byte 72.
            { "80 byte multibyte password", "valid@seathive.test", new string('ğ', 40) }
        };

        [Theory]
        [MemberData(nameof(InvalidRegistrations))]
        public async Task Register_ShouldReturn400_ForInvalidInput(string reason, string email, string password)
        {
            var client = _fixture.Api.CreateClient();

            var response = await ApiFactory.RegisterAsync(client, email, password);

            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{reason}: got {(int)response.StatusCode}");
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }

        [Theory]
        [InlineData("", "Passw0rd!")]
        [InlineData("not-an-email", "Passw0rd!")]
        [InlineData("valid@seathive.test", "")]
        public async Task Login_ShouldReturn400_ForInvalidInput(string email, string password)
        {
            var client = _fixture.Api.CreateClient();

            var response = await client.PostAsJsonAsync("/api/Auth/login", new { email, password });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public async Task Booking_ShouldReturn400_ForInvalidSeatId(int seatId)
        {
            var client = await _fixture.Api.CreateUserClientAsync();

            var response = await client.PostAsJsonAsync("/api/Booking/hold", new { seatId });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
    }
}
