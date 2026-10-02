using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using SeatHive.Api.Data;
using SeatHive.Api.Models;
using SeatHive.Tests.Integration.Api;
using StackExchange.Redis;
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
    }

    [CollectionDefinition(Name)]
    public class ContainersCollection : ICollectionFixture<ContainersFixture>
    {
        public const string Name = "Containers";
    }
}
