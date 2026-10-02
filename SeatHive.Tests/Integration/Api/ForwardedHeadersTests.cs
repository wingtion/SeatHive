using System.Net;

namespace SeatHive.Tests.Integration.Api
{
    // Behind the reverse proxy every request arrives from the address of the proxy. The limit per IP address only
    // works when the address of the client is taken from X-Forwarded-For, and only when a trusted proxy sent it.
    [Collection(ContainersCollection.Name)]
    public class ForwardedHeadersTests
    {
        private const int Limit = 3;
        private const string TrustedProxy = "172.28.0.10";
        private const string Stranger = "203.0.113.9";

        private readonly ContainersFixture _fixture;

        public ForwardedHeadersTests(ContainersFixture fixture)
        {
            _fixture = fixture;
        }

        private ApiFactory CreateApi(string trustedProxies = "172.28.0.0/24")
        {
            return new ApiFactory(_fixture, new Dictionary<string, string?>
            {
                ["RateLimiting__Read__PermitLimit"] = Limit.ToString(),
                ["ReverseProxy__TrustedProxies__0"] = trustedProxies
            });
        }

        // A request as it reaches the API: from connectionIp, saying it was forwarded for forwardedFor.
        private static Task<HttpResponseMessage> GetAsync(HttpClient client, string connectionIp, string forwardedFor)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/Events");
            request.Headers.Add(ApiFactory.RemoteIpHeader, connectionIp);
            request.Headers.Add("X-Forwarded-For", forwardedFor);
            return client.SendAsync(request);
        }

        [Fact]
        public async Task Clients_ShouldEachGetTheirOwnLimit_BehindATrustedProxy()
        {
            await using var api = CreateApi();
            var client = api.CreateClient();

            for (var i = 0; i < Limit; i++)
            {
                Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, TrustedProxy, "198.51.100.1")).StatusCode);
            }

            Assert.Equal(HttpStatusCode.TooManyRequests, (await GetAsync(client, TrustedProxy, "198.51.100.1")).StatusCode);
            // Same proxy, another client: not affected by the first one.
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, TrustedProxy, "198.51.100.2")).StatusCode);
        }

        [Fact]
        public async Task ForwardedAddress_ShouldBeIgnored_WhenTheSenderIsNotATrustedProxy()
        {
            await using var api = CreateApi();
            var client = api.CreateClient();

            // A client that talks to the API directly and invents a new address for every request.
            for (var i = 0; i < Limit; i++)
            {
                Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, Stranger, $"198.51.100.{i + 10}")).StatusCode);
            }

            Assert.Equal(HttpStatusCode.TooManyRequests, (await GetAsync(client, Stranger, "198.51.100.99")).StatusCode);
        }

        [Fact]
        public async Task OnlyTheLastHop_ShouldCount_WhenTheClientSendsItsOwnForwardedAddresses()
        {
            await using var api = CreateApi();
            var client = api.CreateClient();

            // The proxy appends the address it saw; whatever the client put in front of it must not count.
            for (var i = 0; i < Limit; i++)
            {
                Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, TrustedProxy, $"10.9.9.{i}, 198.51.100.50")).StatusCode);
            }

            Assert.Equal(HttpStatusCode.TooManyRequests, (await GetAsync(client, TrustedProxy, "10.9.9.200, 198.51.100.50")).StatusCode);
        }

        [Fact]
        public async Task ForwardedAddress_ShouldBeIgnored_WhenNoProxyIsConfigured()
        {
            await using var api = new ApiFactory(_fixture, new Dictionary<string, string?> { ["RateLimiting__Read__PermitLimit"] = Limit.ToString() });
            var client = api.CreateClient();

            for (var i = 0; i < Limit; i++)
            {
                Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, TrustedProxy, $"198.51.100.{i + 10}")).StatusCode);
            }

            Assert.Equal(HttpStatusCode.TooManyRequests, (await GetAsync(client, TrustedProxy, "198.51.100.99")).StatusCode);
        }

        [Theory]
        [InlineData("not-an-address")]
        [InlineData("172.28.0.0/99")]
        public async Task Startup_ShouldFailWithClearError_WhenATrustedProxyIsInvalid(string value)
        {
            await using var api = CreateApi(value);

            var error = Assert.ThrowsAny<Exception>(() => api.CreateClient());

            Assert.Contains("ReverseProxy:TrustedProxies", error.ToString());
        }
    }
}
