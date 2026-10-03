using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SeatHive.Tests.Integration
{
    [Collection(TestCollections.HoldService)]
    [Trait(TestCategories.Trait, TestCategories.Integration)]
    public class MigrationTests
    {
        private readonly ContainersFixture _fixture;

        public MigrationTests(ContainersFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task AddChargeAnnouncementAndRefund_ShouldTreatExistingChargesAsAnnounced()
        {
            // The Worker's database from before a charge recorded when its result was announced.
            // Those results were all announced right after the charge.
            var options = new DbContextOptionsBuilder<SeatHive.Worker.Data.WorkerDbContext>()
                .UseNpgsql(_fixture.GetPostgresConnectionString("seathive_worker_upgrade"), SeatHive.Worker.Data.WorkerDbContext.ConfigureNpgsql)
                .Options;
            await using var db = new SeatHive.Worker.Data.WorkerDbContext(options);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("AddSimulatedCharges");
            var key = Guid.NewGuid();
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO worker.\"SimulatedCharges\" (\"IdempotencyKey\", \"Succeeded\", \"ChargedAt\") VALUES ({0}, true, '2026-09-01T10:00:00Z')",
                key);

            // A charge made a moment before the upgrade: its result may still be on its way.
            var recentKey = Guid.NewGuid();
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO worker.\"SimulatedCharges\" (\"IdempotencyKey\", \"Succeeded\", \"ChargedAt\") VALUES ({0}, true, now())",
                recentKey);

            await migrator.MigrateAsync();

            var charge = await db.SimulatedCharges.AsNoTracking().SingleAsync(c => c.IdempotencyKey == key);
            // Otherwise the cleanup would keep them forever, as charges whose result is still to be announced.
            Assert.Equal(charge.ChargedAt, charge.AnnouncedAt);
            Assert.Null(charge.RefundedAt);

            // Marking this one as announced would keep its result from being announced when it arrives.
            var recent = await db.SimulatedCharges.AsNoTracking().SingleAsync(c => c.IdempotencyKey == recentKey);
            Assert.Null(recent.AnnouncedAt);
        }

        [Fact]
        public async Task AddUserRoleAndUniqueEmail_ShouldUpgradeExistingUsers()
        {
            // A database as it was before roles existed, with one user registered in mixed case.
            var connectionString = _fixture.GetPostgresConnectionString("seathive_upgrade");
            await using var db = _fixture.CreateContext(connectionString);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("AddUsersTable");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Users\" (\"Email\", \"PasswordHash\") VALUES ('Old.User@SeatHive.Test', 'hash')");

            await migrator.MigrateAsync();

            var user = await db.Users.AsNoTracking().SingleAsync();
            Assert.Equal("old.user@seathive.test", user.Email);
            Assert.Equal("User", user.Role);
        }

        [Fact]
        public async Task AddBookings_ShouldTurnBookedSeatsIntoConfirmedBookings()
        {
            // A database from before the Bookings table: seat 1 booked by a real user,
            // seat 2 booked by a user id that does not exist (the old simulation did that), seat 3 free.
            var connectionString = _fixture.GetPostgresConnectionString("seathive_bookings_upgrade");
            await using var db = _fixture.CreateContext(connectionString);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("RemoveSeatVersion");
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Users" ("Email", "PasswordHash", "Role") VALUES ('real@seathive.test', 'hash', 'User');
                INSERT INTO "Events" ("Name", "Date") VALUES ('Old Event', now());
                INSERT INTO "Seats" ("Section", "Row", "SeatNumber", "EventId", "IsBooked", "UserId") VALUES
                    ('A', '1', 1, 1, true, 1),
                    ('A', '1', 2, 1, true, 1001),
                    ('A', '1', 3, 1, false, NULL);
                """);

            await migrator.MigrateAsync();

            var bookings = await db.Database
                .SqlQueryRaw<string>("SELECT \"SeatId\" || ':' || \"UserId\" || ':' || \"Status\" AS \"Value\" FROM \"Bookings\"")
                .ToListAsync();
            Assert.Equal(new[] { "1:1:Confirmed" }, bookings);
        }
    }
}
