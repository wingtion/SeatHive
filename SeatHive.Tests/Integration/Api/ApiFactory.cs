using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace SeatHive.Tests.Integration.Api
{
    // Runs the real API in-process against the test containers.
    // RabbitMQ is replaced by the MassTransit in-memory test harness.
    public class ApiFactory : WebApplicationFactory<Program>
    {
        // Test-only values; they never reach a real environment.
        public const string JwtKey = "test-only-signing-key-0123456789-abcdefghijklmnop";
        public const string AdminEmail = "admin@seathive.test";
        public const string AdminPassword = "AdminPassw0rd!";

        private static readonly object EnvironmentLock = new();
        private readonly Dictionary<string, string?> _settings;

        public ApiFactory(ContainersFixture fixture, Dictionary<string, string?>? overrides = null)
        {
            _settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings__DefaultConnection"] = fixture.GetPostgresConnectionString(),
                ["ConnectionStrings__Redis"] = fixture.RedisConnectionString,
                ["Jwt__Key"] = JwtKey,
                ["RabbitMQ__HostName"] = "localhost",
                ["RabbitMQ__Username"] = "test",
                ["RabbitMQ__Password"] = "test",
                ["SEATHIVE_ADMIN_EMAIL"] = AdminEmail,
                ["SEATHIVE_ADMIN_PASSWORD"] = AdminPassword,
                // High limits so only the rate limit tests ever hit them.
                ["RateLimiting__Auth__PermitLimit"] = "100000",
                ["RateLimiting__Booking__PermitLimit"] = "100000"
            };

            foreach (var (key, value) in overrides ?? new())
            {
                _settings[key] = value;
            }
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Not "Development", so user-secrets on the developer machine cannot leak into tests.
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services => services.AddMassTransitTestHarness());
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            // Program.cs reads configuration while it starts, so the settings are handed over
            // as environment variables for the duration of the startup only. A null value unsets a variable.
            lock (EnvironmentLock)
            {
                var previous = _settings.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
                try
                {
                    foreach (var (key, value) in _settings) Environment.SetEnvironmentVariable(key, value);
                    return base.CreateHost(builder);
                }
                finally
                {
                    foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
                }
            }
        }

        public static string UniqueEmail() => $"user-{Guid.NewGuid():N}@seathive.test";

        public static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email, string password)
        {
            return client.PostAsJsonAsync("/api/Auth/register", new { email, password });
        }

        public static async Task<string> LoginAsync(HttpClient client, string email, string password)
        {
            var response = await client.PostAsJsonAsync("/api/Auth/login", new { email, password });
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return body.GetProperty("token").GetString()!;
        }

        public async Task<HttpClient> CreateUserClientAsync(string? email = null, string password = "Passw0rd!")
        {
            email ??= UniqueEmail();
            var client = CreateClient();
            (await RegisterAsync(client, email, password)).EnsureSuccessStatusCode();
            return Authorize(client, await LoginAsync(client, email, password));
        }

        public async Task<HttpClient> CreateAdminClientAsync()
        {
            var client = CreateClient();
            return Authorize(client, await LoginAsync(client, AdminEmail, AdminPassword));
        }

        public static HttpClient Authorize(HttpClient client, string token)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }
    }
}
