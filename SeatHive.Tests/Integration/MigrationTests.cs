using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SeatHive.Tests.Integration
{
    [Collection(ContainersCollection.Name)]
    public class MigrationTests
    {
        private readonly ContainersFixture _fixture;

        public MigrationTests(ContainersFixture fixture)
        {
            _fixture = fixture;
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
    }
}
