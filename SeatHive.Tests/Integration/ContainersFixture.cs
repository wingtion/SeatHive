using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SeatHive.Api.Data;
using SeatHive.Api.Models;
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

        public async Task InitializeAsync()
        {
            await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

            Redis = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());

            await using var db = CreateContext();
            await db.Database.EnsureCreatedAsync();
        }

        public async Task DisposeAsync()
        {
            await Redis.DisposeAsync();
            await _postgres.DisposeAsync();
            await _redis.DisposeAsync();
        }

        public AppDbContext CreateContext(params IInterceptor[] interceptors)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(_postgres.GetConnectionString())
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
