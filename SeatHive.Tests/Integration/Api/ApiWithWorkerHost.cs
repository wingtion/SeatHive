using Microsoft.EntityFrameworkCore;
using SeatHive.Worker.Data;

namespace SeatHive.Tests.Integration.Api
{
    // The API and the Worker's consumers together on one in-memory bus, started once for a test class.
    // It has a database of its own: a host delivers what it finds in the outbox table of its database to its
    // own bus, so two hosts on one database would take each other's events.
    public abstract class ApiWithWorkerHost : IAsyncLifetime
    {
        protected ApiWithWorkerHost(ContainersFixture fixture, string database)
        {
            Fixture = fixture;
            Database = fixture.GetPostgresConnectionString(database);
            Api = Create();
        }

        public ContainersFixture Fixture { get; }

        // The connection string of this host's database. The API creates and migrates it at startup.
        public string Database { get; }

        public ApiFactory Api { get; }

        // Another host like this one, for a test that needs different settings. Give it a database of its own
        // while this host is running.
        public ApiFactory Create(string? database = null, int paymentDelayMs = 0)
        {
            return new ApiFactory(
                Fixture,
                new Dictionary<string, string?> { ["ConnectionStrings__DefaultConnection"] = database ?? Database },
                withWorker: true,
                paymentDelayMs: paymentDelayMs);
        }

        // The Worker creates its own tables at startup; here that is done for the Worker's part of a host.
        public static async Task MigrateWorkerAsync(string database)
        {
            var options = new DbContextOptionsBuilder<WorkerDbContext>()
                .UseNpgsql(database, WorkerDbContext.ConfigureNpgsql)
                .Options;
            await using var db = new WorkerDbContext(options);
            await db.Database.MigrateAsync();
        }

        public Task InitializeAsync() => MigrateWorkerAsync(Database);

        public async Task DisposeAsync() => await Api.DisposeAsync();
    }

    public sealed class HistoryHost : ApiWithWorkerHost
    {
        public HistoryHost(ContainersFixture fixture) : base(fixture, "seathive_history")
        {
        }
    }

    public sealed class EndToEndHost : ApiWithWorkerHost
    {
        public EndToEndHost(ContainersFixture fixture) : base(fixture, "seathive_e2e")
        {
        }
    }
}
