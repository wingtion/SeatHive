using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SeatHive.Api.Services;

namespace SeatHive.Tests.Integration.Api
{
    // What a client gets when something the API depends on fails.
    [Collection(TestCollections.ApiBooking)]
    [Trait(TestCategories.Trait, TestCategories.Api)]
    public class FailureResponseTests
    {
        private const string HoldUrl = "/api/Booking/hold";

        private readonly ContainersFixture _fixture;

        public FailureResponseTests(ContainersFixture fixture)
        {
            _fixture = fixture;
        }

        // The Redis lock only keeps concurrent requests apart; the database decides who gets a seat.
        // So the API starts without Redis, and a hold still works.
        [Fact]
        public async Task Hold_ShouldSucceed_WhenRedisCannotBeReached()
        {
            await using var api = new ApiFactory(_fixture, new Dictionary<string, string?>
            {
                ["ConnectionStrings__Redis"] = RedisLockServiceTests.UnreachableRedis
            });
            var client = await api.CreateUserClientAsync();
            var seatId = await _fixture.CreateFreeSeatAsync();

            var response = await client.PostAsJsonAsync(HoldUrl, new { seatId });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("held", body.GetProperty("status").GetString());
        }

        private sealed class BrokenLockService : IRedisLockService
        {
            public const string Detail = "internal detail that must not reach the client";

            public Task<IAsyncDisposable?> AcquireLockAsync(string key, TimeSpan expiry)
            {
                throw new InvalidOperationException(Detail);
            }
        }

        // An error nobody expected gets the same ProblemDetails body as every other error, and says nothing
        // about what went wrong inside: that is in the log only.
        [Fact]
        public async Task UnexpectedError_ShouldGetAProblemBody_WithoutDetails()
        {
            var (_, token, _) = await _fixture.Api.CreateUserAsync();
            var seatId = await _fixture.CreateFreeSeatAsync();
            await using var broken = _fixture.Api.WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services => services.AddScoped<IRedisLockService, BrokenLockService>()));
            var client = ApiFactory.Authorize(broken.CreateClient(), token);

            var response = await client.PostAsJsonAsync(HoldUrl, new { seatId });

            await ProblemAssert.HasCodeAsync(response, HttpStatusCode.InternalServerError, ErrorCodes.InternalError);
            Assert.DoesNotContain(BrokenLockService.Detail, await response.Content.ReadAsStringAsync());
        }
    }
}
