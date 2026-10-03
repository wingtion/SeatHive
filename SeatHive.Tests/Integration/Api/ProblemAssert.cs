using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SeatHive.Tests.Integration.Api
{
    public static class ProblemAssert
    {
        // Every error body is ProblemDetails with a machine-readable "code".
        public static async Task HasCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
        {
            Assert.Equal(status, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.TryGetProperty("code", out var actualCode), "The problem body has no \"code\".");
            Assert.Equal(code, actualCode.GetString());
        }
    }
}
