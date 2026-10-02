using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MassTransit;
using MassTransit.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SeatHive.Api.Services;
using Microsoft.EntityFrameworkCore;
using SeatHive.Worker;
using SeatHive.Worker.Data;
using SeatHive.Worker.Payments;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

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

        // The API's clock. It starts at the real time (whole seconds, so values survive the database round trip)
        // and never moves by itself, so holds do not expire and the sweeper does not run unless a test advances it.
        public GatedClock Clock { get; } =
            new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

        // Records what the API publishes and consumes on the in-memory bus.
        public ITestHarness Harness => Services.GetRequiredService<ITestHarness>();

        // Waits until an event has come out of the outbox and reached a subscriber in this host.
        public Task<T> WaitForDeliveryAsync<T>(Func<T, bool> match) where T : class
        {
            return Services.GetRequiredService<EventLog>().WaitForAsync(match);
        }

        public int CountDelivered<T>(Func<T, bool> match) where T : class
        {
            return Services.GetRequiredService<EventLog>().Of(match).Count;
        }

        private static readonly object EnvironmentLock = new();
        private readonly Dictionary<string, string?> _settings;
        private readonly bool _withWorker;
        private readonly int _paymentDelayMs;

        // withWorker also runs the Worker's consumers in this host, with payments that never fail at random and take
        // paymentDelayMs on the test clock (no time by default), so a booking goes through its whole story
        // without a test publishing anything by hand.
        public ApiFactory(ContainersFixture fixture, Dictionary<string, string?>? overrides = null, bool withWorker = false, int paymentDelayMs = 0)
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

            _withWorker = withWorker;
            _paymentDelayMs = paymentDelayMs;

            foreach (var (key, value) in overrides ?? new())
            {
                _settings[key] = value;
            }
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Not "Development", so user-secrets on the developer machine cannot leak into tests.
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                // The harness rebuilds the bus on the in-memory transport. It keeps the consumers with their
                // endpoint configuration but drops the outbox, so the API's own outbox setup is applied again.
                var configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["Outbox:QueryDelayMs"] = "50" })
                    .Build();
                services.AddMassTransitTestHarness(x =>
                {
                    x.AddBookingOutbox(configuration);
                    x.AddEventRecorders();
                    if (_withWorker)
                    {
                        // The Worker's consumers with the Worker's own inbox and outbox, as in its own process.
                        x.AddWorkerConsumers();
                        x.AddWorkerOutbox();
                    }
                });
                services.AddSingleton<TimeProvider>(Clock);
                if (_withWorker)
                {
                    // What the Worker's consumers need: its payment settings and the simulated provider with its table.
                    services.Configure<PaymentOptions>(o =>
                    {
                        o.FailureRate = 0;
                        o.MinDelayMs = _paymentDelayMs;
                        o.MaxDelayMs = _paymentDelayMs;
                    });
                    services.AddDbContext<WorkerDbContext>(o => o.UseNpgsql(
                        _settings["ConnectionStrings__DefaultConnection"], WorkerDbContext.ConfigureNpgsql));
                    services.AddScoped<ISimulatedPaymentProvider, SimulatedPaymentProvider>();
                }
            });
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
