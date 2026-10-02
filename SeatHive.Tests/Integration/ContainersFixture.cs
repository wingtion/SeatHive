using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using SeatHive.Api.Data;
using SeatHive.Api.Models;
using SeatHive.Api.Services;
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

        public async Task<int> CreateFreeSeatAsync()
        {
            await using var db = CreateContext();
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
