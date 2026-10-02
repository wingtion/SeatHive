using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    // Starts one real Postgres and one real Redis for all integration tests.
    public class ContainersFixture : IAsyncLifetime
    {
        private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();

        private readonly RedisContainer _redis = new RedisBuilder("redis:alpine").Build();

        public IConnectionMultiplexer Redis { get; private set; } = null!;

        public string RedisConnectionString => _redis.GetConnectionString();

        // The API under test, wired to the containers above.
        public ApiFactory Api { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

            Redis = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());

            await using var db = CreateContext();
            await db.Database.MigrateAsync();

            Api = new ApiFactory(this);
        }

        public async Task DisposeAsync()
        {
            await Api.DisposeAsync();
            await Redis.DisposeAsync();
            await _postgres.DisposeAsync();
            await _redis.DisposeAsync();
        }

        // Connection string for the default test database, or for another database on the same server.
        public string GetPostgresConnectionString(string? database = null)
        {
            var builder = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString());
            if (database != null) builder.Database = database;
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
                lockService ?? new RedisLockService(Redis),
                bus ?? Mock.Of<IPublishEndpoint>(),
                clock,
                Options.Create(options ?? new HoldOptions()));
        }
    }

    [CollectionDefinition(Name)]
    public class ContainersCollection : ICollectionFixture<ContainersFixture>
    {
        public const string Name = "Containers";
    }
}
