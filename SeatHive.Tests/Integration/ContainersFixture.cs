using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using SeatHive.Api.Data;
using SeatHive.Api.Models;
using SeatHive.Api.Services;
using SeatHive.Shared.Events;
using SeatHive.Tests.Integration.Api;
using StackExchange.Redis;
using Event = SeatHive.Api.Models.Event;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace SeatHive.Tests.Integration
{
    // One real Postgres and one real Redis for the whole test run, started once, by whoever needs them first.
    // They are not stopped here: Testcontainers removes them when the test process ends.
    public static class SharedContainers
    {
        // The same images as docker-compose.yml (see ComposeImages).
        // Test collections run in parallel and each keeps its own connections, so the server allows more than its default 100.
        private static readonly PostgreSqlContainer Postgres = new PostgreSqlBuilder(ComposeImages.Of("postgres"))
            .WithCommand("-c", "max_connections=400")
            .Build();

        private static readonly RedisContainer Redis = new RedisBuilder(ComposeImages.Of("redis")).Build();

        private static readonly Lazy<Task> Started = new(() => Task.WhenAll(Postgres.StartAsync(), Redis.StartAsync()));

        private static int _fixtures;

        public static Task StartAsync() => Started.Value;

        public static string PostgresConnectionString => Postgres.GetConnectionString();

        public static string RedisConnectionString => Redis.GetConnectionString();

        // A number for each fixture, which names its database and picks its Redis database.
        public static int NextFixtureNumber() => Interlocked.Increment(ref _fixtures);
    }

    // What one test collection works on: a database of its own on the shared Postgres server and a Redis database
    // of its own on the shared Redis. Collections run in parallel, and they must not see each other's data:
    // a reset of the demo data empties the tables, seat ids (and with them the lock keys) start at 1 in every database,
    // and an API host delivers whatever it finds in the outbox table of its database.
    public class ContainersFixture : IAsyncLifetime
    {
        private int _number;
        private string _database = null!;

        public IConnectionMultiplexer Redis { get; private set; } = null!;

        public string RedisConnectionString => $"{SharedContainers.RedisConnectionString},defaultDatabase={_number}";

        // The API under test, wired to this fixture's databases. It starts when a test first uses it.
        public ApiFactory Api { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            await SharedContainers.StartAsync();

            _number = SharedContainers.NextFixtureNumber();
            _database = $"seathive_tests_{_number}";

            Redis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);

            await using var db = CreateContext();
            await db.Database.MigrateAsync();

            Api = new ApiFactory(this);
        }

        public async ValueTask DisposeAsync()
        {
            await Api.DisposeAsync();
            await Redis.DisposeAsync();
        }

        // Connection string for this fixture's database, or for another database on the same server.
        public string GetPostgresConnectionString(string? database = null)
        {
            var builder = new NpgsqlConnectionStringBuilder(SharedContainers.PostgresConnectionString)
            {
                Database = database ?? _database
            };
            return builder.ConnectionString;
        }

        public AppDbContext CreateContext(params IInterceptor[] interceptors)
        {
            return CreateContext(GetPostgresConnectionString(), interceptors);
        }

        public AppDbContext CreateContext(string connectionString, params IInterceptor[] interceptors)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(connectionString)
                .AddInterceptors(interceptors)
                .Options;
            return new AppDbContext(options);
        }

        // connectionString picks another database on the same server; the default is the shared test database.
        public async Task<int> CreateFreeSeatAsync(string? connectionString = null)
        {
            await using var db = CreateContext(connectionString ?? GetPostgresConnectionString());
            var seat = new Seat
            {
                Section = "A",
                Row = "1",
                SeatNumber = 1,
                Event = new Event { Name = "Test Event", Date = DateTime.UtcNow }
            };
            db.Seats.Add(seat);
            await db.SaveChangesAsync();
            return seat.Id;
        }

        // An event of its own with seats A-1-1 .. A-1-n, returned in that order.
        public async Task<(int EventId, List<int> SeatIds)> CreateEventAsync(int seats, string? connectionString = null)
        {
            await using var db = CreateContext(connectionString ?? GetPostgresConnectionString());
            var created = new Event
            {
                Name = "Test Event",
                Date = DateTime.UtcNow,
                Seats = Enumerable.Range(1, seats)
                    .Select(number => new Seat { Section = "A", Row = "1", SeatNumber = number })
                    .ToList()
            };
            db.Events.Add(created);
            await db.SaveChangesAsync();
            return (created.Id, created.Seats.OrderBy(s => s.SeatNumber).Select(s => s.Id).ToList());
        }

        // Bookings reference real users, so tests that book through the service need some.
        public async Task<List<int>> CreateUsersAsync(int count)
        {
            await using var db = CreateContext();
            var users = Enumerable.Range(0, count)
                .Select(_ => new User { Email = ApiFactory.UniqueEmail(), PasswordHash = "not-a-real-hash" })
                .ToList();
            db.Users.AddRange(users);
            await db.SaveChangesAsync();
            return users.Select(u => u.Id).ToList();
        }

        public async Task<int> CreateUserAsync() => (await CreateUsersAsync(1)).Single();

        public async Task<Booking> ReadBookingAsync(int bookingId, string? connectionString = null)
        {
            await using var db = CreateContext(connectionString ?? GetPostgresConnectionString());
            return await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        }

        // Messages are consumed in the background, so the outcome is polled for. Fails after 10 seconds.
        public async Task<Booking> WaitForBookingStatusAsync(int bookingId, BookingStatus status, string? connectionString = null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                var booking = await ReadBookingAsync(bookingId, connectionString);
                if (booking.Status == status) return booking;

                Assert.True(DateTime.UtcNow < deadline, $"Booking {bookingId} is {booking.Status}, expected {status}.");
                await Task.Delay(20);
            }
        }

        // Takes a hold all the way to Confirmed through the API: confirm, then a successful payment result.
        // The API host has no payment simulator (that is the Worker), so the result is published by hand.
        public async Task ConfirmThroughPaymentAsync(HttpClient client, int bookingId)
        {
            var response = await client.PostAsync($"/api/Booking/{bookingId}/confirm", null);
            Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);

            var booking = await ReadBookingAsync(bookingId);
            await Api.Harness.Bus.Publish(
                new PaymentSucceeded(booking.Id, booking.SeatId, booking.UserId, DateTime.UtcNow, booking.PaymentId!.Value));
            await WaitForBookingStatusAsync(bookingId, BookingStatus.Confirmed);
        }

        // How many other sessions in the database have a transaction open and are doing nothing in it.
        // Short transactions that are just finishing are given two seconds to end, so the answer is 0 unless
        // somebody really keeps one open, for example while waiting for something outside the database.
        public async Task<int> CountOpenTransactionsAsync(string connectionString)
        {
            await using var db = CreateContext(connectionString);
            var deadline = DateTime.UtcNow.AddSeconds(2);

            while (true)
            {
                var open = await db.Database
                    .SqlQueryRaw<int>(
                        "SELECT COUNT(*)::int AS \"Value\" FROM pg_stat_activity " +
                        "WHERE datname = current_database() AND pid <> pg_backend_pid() AND state = 'idle in transaction'")
                    .SingleAsync();
                if (open == 0 || DateTime.UtcNow >= deadline) return open;

                await Task.Delay(20);
            }
        }

        // The "payment succeeded" message as the Worker would send it.
        public static PaymentSucceeded SuccessfulPayment(int bookingId, Guid paymentId, int seatId = 0, int userId = 0)
        {
            return new PaymentSucceeded(bookingId, seatId, userId, DateTime.UtcNow, paymentId);
        }

        // The events of one type that a service published to a mocked bus, in order.
        public static List<T> PublishedTo<T>(Mock<IPublishEndpoint> bus)
        {
            return bus.Invocations
                .Where(i => i.Method.Name == nameof(IPublishEndpoint.Publish))
                .Select(i => i.Arguments[0])
                .OfType<T>()
                .ToList();
        }

        // A BookingService on the given context. Anything not passed in gets a default:
        // the real Redis lock, a bus that swallows events and the default hold settings.
        public BookingService CreateBookingService(
            AppDbContext db,
            TimeProvider clock,
            IRedisLockService? lockService = null,
            IPublishEndpoint? bus = null,
            HoldOptions? options = null)
        {
            return new BookingService(
                db,
                lockService ?? new RedisLockService(Redis, NullLogger<RedisLockService>.Instance),
                bus ?? Mock.Of<IPublishEndpoint>(),
                clock,
                Options.Create(options ?? new HoldOptions()),
                NullLogger<BookingService>.Instance);
        }
    }

    // The test collections. Tests inside one collection run one after the other and share a fixture, which means
    // a database; different collections run in parallel, each on its own. A class goes where the classes are
    // that it may share a database with.
    public static class TestCollections
    {
        // API tests on the fixture's host that hold, pay and reset.
        public const string ApiBooking = "Api: booking";
        // API tests that read, sign in, or start short-lived hosts of their own.
        public const string ApiRead = "Api: read and access";
        // API and Worker together, on databases and hosts of their own.
        public const string History = "Api and Worker: history";
        public const string EndToEnd = "Api and Worker: end to end";
        public const string Live = "Api and Worker: live updates";
        // The race simulation: it moves its host's clock and starts hosts of its own on its database.
        public const string Simulation = "Api: simulation";
        // The nightly reset and what a reset removes: only hosts of their own, whose clocks they move by hours.
        public const string DemoReset = "Api: demo reset";
        // The booking service on the database, without an API host.
        public const string HoldService = "Service: holds";
        public const string PaymentService = "Service: payments";
        // The Worker's consumers and provider.
        public const string Worker = "Worker";

        [CollectionDefinition(ApiBooking)]
        public class ApiBookingCollection : ICollectionFixture<ContainersFixture> { }

        [CollectionDefinition(ApiRead)]
        public class ApiReadCollection : ICollectionFixture<ContainersFixture> { }

        [CollectionDefinition(History)]
        public class HistoryCollection : ICollectionFixture<ContainersFixture> { }

        [CollectionDefinition(Live)]
        public class LiveCollection : ICollectionFixture<ContainersFixture> { }

        [CollectionDefinition(Simulation)]
        public class SimulationCollection : ICollectionFixture<ContainersFixture> { }

        [CollectionDefinition(DemoReset)]
        public class DemoResetCollection : ICollectionFixture<ContainersFixture> { }

        [CollectionDefinition(EndToEnd)]
        public class EndToEndCollection : ICollectionFixture<ContainersFixture> { }

        [CollectionDefinition(HoldService)]
        public class HoldServiceCollection : ICollectionFixture<ContainersFixture> { }

        [CollectionDefinition(PaymentService)]
        public class PaymentServiceCollection : ICollectionFixture<ContainersFixture> { }

        [CollectionDefinition(Worker)]
        public class WorkerCollection : ICollectionFixture<ContainersFixture> { }
    }

    // What a test needs to run, for --filter "Category=...". Every test needs Docker; none is a unit test.
    public static class TestCategories
    {
        public const string Trait = "Category";

        // A service or the schema on the real database, no API host.
        public const string Integration = "Integration";
        // The API in-process, over HTTP.
        public const string Api = "Api";
        // The API and the Worker's consumers together.
        public const string EndToEnd = "E2E";
    }
}
