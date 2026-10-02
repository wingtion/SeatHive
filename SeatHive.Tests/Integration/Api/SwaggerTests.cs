using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SeatHive.Tests.Integration.Api
{
    [Collection(ContainersCollection.Name)]
    public class SwaggerTests
    {
        private const string DocumentUrl = "/swagger/v1/swagger.json";

        private readonly ContainersFixture _fixture;

        public SwaggerTests(ContainersFixture fixture)
        {
            _fixture = fixture;
        }

        private async Task<JsonElement> ReadDocumentAsync()
        {
            await using var api = new ApiFactory(_fixture, environment: "Development");

            var response = await api.CreateClient().GetAsync(DocumentUrl);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        private static bool HasSecurity(JsonElement document, string path, string method)
        {
            var operation = document.GetProperty("paths").GetProperty(path).GetProperty(method);
            return operation.TryGetProperty("security", out var security) && security.GetArrayLength() > 0;
        }

        [Fact]
        public async Task Document_ShouldBeServed_InDevelopment()
        {
            var document = await ReadDocumentAsync();

            Assert.Equal("SeatHive API", document.GetProperty("info").GetProperty("title").GetString());
        }

        [Theory]
        [InlineData("Testing")]
        [InlineData("Production")]
        public async Task Swagger_ShouldNotBeServed_OutsideDevelopment(string environment)
        {
            await using var api = new ApiFactory(_fixture, environment: environment);
            var client = api.CreateClient();

            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(DocumentUrl)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/swagger/index.html")).StatusCode);
        }

        [Fact]
        public async Task SecurityScheme_ShouldBeHttpBearer()
        {
            var document = await ReadDocumentAsync();

            var scheme = document.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
            Assert.Equal("http", scheme.GetProperty("type").GetString());
            Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());
            Assert.Equal("JWT", scheme.GetProperty("bearerFormat").GetString());
        }

        [Theory]
        [InlineData("/api/Booking/hold", "post")]
        [InlineData("/api/Booking/{id}/confirm", "post")]
        [InlineData("/api/Booking/{id}/release", "post")]
        [InlineData("/api/Setup/create-data", "post")]
        [InlineData("/api/Simulation/simulate-concurrency", "post")]
        public async Task AuthorizedEndpoint_ShouldRequireTheBearerScheme(string path, string method)
        {
            var document = await ReadDocumentAsync();

            var security = document.GetProperty("paths").GetProperty(path).GetProperty(method).GetProperty("security");
            Assert.True(security[0].TryGetProperty("Bearer", out _));
        }

        [Theory]
        [InlineData("/api/Auth/register", "post")]
        [InlineData("/api/Auth/login", "post")]
        public async Task AnonymousEndpoint_ShouldNotRequireSecurity(string path, string method)
        {
            var document = await ReadDocumentAsync();

            Assert.False(HasSecurity(document, path, method));
            // Nothing is required for the whole document either; that would cover the anonymous endpoints too.
            Assert.False(document.TryGetProperty("security", out var global) && global.GetArrayLength() > 0);
        }
    }
}
