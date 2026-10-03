using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SeatHive.Tests.Integration.Api
{
    // A browser on another origin (the front end) may only call the API when the API names that origin.
    // Only the configured origins are named, never "*", and with credentials allowed.
    [Collection(TestCollections.ApiRead)]
    [Trait(TestCategories.Trait, TestCategories.Api)]
    public class CorsTests
    {
        private const string FrontEnd = "https://seathive-front.example";
        private const string Stranger = "https://elsewhere.example";

        private readonly ContainersFixture _fixture;

        public CorsTests(ContainersFixture fixture)
        {
            _fixture = fixture;
        }

        private ApiFactory CreateApi(params string[] origins)
        {
            var settings = new Dictionary<string, string?>();
            for (var i = 0; i < origins.Length; i++) settings[$"Cors__AllowedOrigins__{i}"] = origins[i];
            return new ApiFactory(_fixture, settings);
        }

        // What a browser asks before a request it may not send on its own.
        private static Task<HttpResponseMessage> PreflightAsync(HttpClient client, string path, string origin, string method, string headers)
        {
            var request = new HttpRequestMessage(HttpMethod.Options, path);
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", method);
            request.Headers.Add("Access-Control-Request-Headers", headers);
            return client.SendAsync(request);
        }

        private static string? Header(HttpResponseMessage response, string name)
        {
            return response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
        }

        [Theory]
        [InlineData("/api/Events", "GET", "authorization")]
        [InlineData("/api/Booking/hold", "POST", "authorization,content-type")]
        [InlineData("/hubs/seats/negotiate", "POST", "authorization,x-requested-with,x-signalr-user-agent")]
        public async Task Preflight_ShouldBeAllowed_FromAConfiguredOrigin(string path, string method, string headers)
        {
            await using var api = CreateApi(FrontEnd);

            var response = await PreflightAsync(api.CreateClient(), path, FrontEnd, method, headers);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(FrontEnd, Header(response, "Access-Control-Allow-Origin"));
            Assert.Equal("true", Header(response, "Access-Control-Allow-Credentials"));
            Assert.Contains(method, Header(response, "Access-Control-Allow-Methods"));
            foreach (var header in headers.Split(','))
            {
                Assert.Contains(header, Header(response, "Access-Control-Allow-Headers")!, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public async Task Request_ShouldNameTheOrigin_WhenItIsConfigured()
        {
            await using var api = CreateApi(FrontEnd);
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/Events");
            request.Headers.Add("Origin", FrontEnd);

            var response = await api.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(FrontEnd, Header(response, "Access-Control-Allow-Origin"));
            Assert.Equal("true", Header(response, "Access-Control-Allow-Credentials"));
        }

        [Fact]
        public async Task AnotherOrigin_ShouldGetNoCorsHeaders()
        {
            await using var api = CreateApi(FrontEnd);
            var client = api.CreateClient();

            var preflight = await PreflightAsync(client, "/api/Booking/hold", Stranger, "POST", "authorization,content-type");
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/Events");
            request.Headers.Add("Origin", Stranger);
            var get = await client.SendAsync(request, TestContext.Current.CancellationToken);

            foreach (var response in new[] { preflight, get })
            {
                Assert.Null(Header(response, "Access-Control-Allow-Origin"));
                Assert.Null(Header(response, "Access-Control-Allow-Credentials"));
            }
        }

        // Only GET and POST: the API has no other method a browser needs. The answer still names the origin,
        // but not the method, so the browser does not send the request.
        [Theory]
        [InlineData("PUT")]
        [InlineData("DELETE")]
        public async Task Preflight_ShouldNotAllowOtherMethods(string method)
        {
            await using var api = CreateApi(FrontEnd);

            var response = await PreflightAsync(api.CreateClient(), "/api/Booking/hold", FrontEnd, method, "authorization");

            Assert.Equal(new[] { "GET", "POST" }, Header(response, "Access-Control-Allow-Methods")!.Split(',').Order());
        }

        [Fact]
        public async Task Cors_ShouldBeOff_WhenNoOriginIsConfigured()
        {
            // Empty entries (an unset variable in docker compose) count as nothing.
            await using var api = CreateApi("");

            var response = await PreflightAsync(api.CreateClient(), "/api/Events", FrontEnd, "GET", "authorization");

            Assert.Null(Header(response, "Access-Control-Allow-Origin"));
        }

        // CORS does not cover the WebSocket handshake; the WebSocket middleware checks the origin against the same list.
        // (The test server hands out WebSockets itself and skips that middleware, so here only the list is checked.)
        [Fact]
        public async Task WebSockets_ShouldOnlyAcceptTheConfiguredOrigins()
        {
            await using var api = CreateApi(FrontEnd);
            api.CreateClient();

            var options = api.Services.GetRequiredService<IOptions<WebSocketOptions>>().Value;

            Assert.Equal(new[] { FrontEnd }, options.AllowedOrigins);
        }

        // The Development settings let the local front end (Vite on port 5173) in; other environments name nobody.
        [Theory]
        [InlineData("Development", "http://localhost:5173")]
        [InlineData("Production", null)]
        public async Task LocalFrontEnd_ShouldOnlyBeAllowed_InDevelopment(string environment, string? expected)
        {
            await using var api = new ApiFactory(_fixture, environment: environment);

            var response = await PreflightAsync(api.CreateClient(), "/api/Events", "http://localhost:5173", "GET", "authorization");

            Assert.Equal(expected, Header(response, "Access-Control-Allow-Origin"));
        }

        [Theory]
        [InlineData("*")]
        [InlineData("https://*.netlify.app")]
        [InlineData("seathive-front.example")]
        [InlineData("ftp://seathive-front.example")]
        [InlineData("https://seathive-front.example/")]
        [InlineData("https://seathive-front.example/app")]
        [InlineData("https://seathive-front.example?x=1")]
        [InlineData("https://user@seathive-front.example")]
        public async Task Startup_ShouldFailWithClearError_WhenAnOriginIsInvalid(string origin)
        {
            await using var api = CreateApi(FrontEnd, origin);

            var error = Assert.ThrowsAny<Exception>(() => api.CreateClient());

            Assert.Contains("Cors:AllowedOrigins", error.ToString());
        }
    }
}
