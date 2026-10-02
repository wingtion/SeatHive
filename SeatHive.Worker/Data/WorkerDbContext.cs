using MassTransit;
using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace SeatHive.Worker.Data
{
    // The Worker keeps the MassTransit inbox and outbox tables, so a message that is delivered twice is handled once
    // and its result is stored with it in one transaction, and the charges of the simulated payment provider.
    // They live in their own schema of the same database the API uses.
    public class WorkerDbContext : DbContext
    {
        public const string Schema = "worker";

        public WorkerDbContext(DbContextOptions<WorkerDbContext> options) : base(options)
        {
        }

        public DbSet<SimulatedCharge> SimulatedCharges { get; set; }

        // The migration history is kept in the Worker's schema too, apart from the API's.
        public static void ConfigureNpgsql(NpgsqlDbContextOptionsBuilder npgsql)
        {
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", Schema);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasDefaultSchema(Schema);

            // The primary key is what makes a charge happen once per key, even for two requests at the same moment.
            modelBuilder.Entity<SimulatedCharge>().HasKey(c => c.IdempotencyKey);

            modelBuilder.AddInboxStateEntity();
            modelBuilder.AddOutboxMessageEntity();
            modelBuilder.AddOutboxStateEntity();
        }
    }
}
