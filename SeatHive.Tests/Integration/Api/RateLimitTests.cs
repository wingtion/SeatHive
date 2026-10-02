using System.Net;
using System.Net.Http.Json;

namespace SeatHive.Tests.Integration.Api
{
    [Collection(ContainersCollection.Name)]
    public class RateLimitTests
    {
        private const int Limit = 3;

        private readonly ContainersFixture _fixture;

        public RateLimitTests(ContainersFixture fixture)
        {
            _fixture = fixture;
        }

        // Each test gets its own API instance, so it starts with an empty rate limit window.
        private ApiFactory CreateLimitedApi()
        {
            return new ApiFactory(_fixture, new Dictionary<string, string?>
            {
                ["RateLimiting__Auth__PermitLimit"] = Limit.ToString(),
                ["RateLimiting__Booking__PermitLimit"] = Limit.ToString()
            });
        }

        [Fact]
        public async Task Auth_ShouldReturn429_WhenLimitIsExceeded()
        {
            await using var api = CreateLimitedApi();
            var client = api.CreateClient();
            var login = new { email = ApiFactory.UniqueEmail(), password = "WrongPassw0rd!" };

            for (var i = 0; i < Limit; i++)
            {
                var allowed = await client.PostAsJsonAsync("/api/Auth/login", login);
                Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
            }

            var rejected = await client.PostAsJsonAsync("/api/Auth/login", login);

            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        }

        [Fact]
        public async Task Booking_ShouldReturn429_WhenLimitIsExceeded()
        {
            await using var api = CreateLimitedApi();
            // Register + login use 2 of the 3 auth permits.
            var client = await api.CreateUserClientAsync();
            var seatId = await _fixture.CreateFreeSeatAsync();

            for (var i = 0; i < Limit; i++)
            {
                var allowed = await client.PostAsJsonAsync("/api/Booking/hold", new { seatId });
                Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
            }

            var rejected = await client.PostAsJsonAsync("/api/Booking/hold", new { seatId });

            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        }
    }
}
