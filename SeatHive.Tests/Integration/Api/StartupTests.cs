using Microsoft.EntityFrameworkCore;

namespace SeatHive.Tests.Integration.Api
{
    [Collection(TestCollections.ApiRead)]
    [Trait(TestCategories.Trait, TestCategories.Api)]
    public class StartupTests
    {
        private readonly ContainersFixture _fixture;

        public StartupTests(ContainersFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task Startup_ShouldFailWithClearError_WhenJwtKeyIsMissing()
        {
            await using var api = new ApiFactory(_fixture, new Dictionary<string, string?> { ["Jwt__Key"] = null });

            var error = Assert.ThrowsAny<Exception>(() => api.CreateClient());

            Assert.Contains("Jwt:Key", error.ToString());
        }

        [Fact]
        public async Task Startup_ShouldFailWithClearError_WhenJwtKeyIsTooShort()
        {
            await using var api = new ApiFactory(_fixture, new Dictionary<string, string?> { ["Jwt__Key"] = "too-short" });

            var error = Assert.ThrowsAny<Exception>(() => api.CreateClient());

            Assert.Contains("Jwt:Key", error.ToString());
        }

        [Fact]
        public async Task Startup_ShouldNotCreateAdmin_WhenAdminVariablesAreMissing()
        {
            // A separate, empty database: the shared one already has the seeded admin.
            var connectionString = _fixture.GetPostgresConnectionString("seathive_no_admin");

            await using var api = new ApiFactory(_fixture, new Dictionary<string, string?>
            {
                ["ConnectionStrings__DefaultConnection"] = connectionString,
                ["SEATHIVE_ADMIN_EMAIL"] = null,
                ["SEATHIVE_ADMIN_PASSWORD"] = null
            });
            api.CreateClient();

            await using var db = _fixture.CreateContext(connectionString);
            Assert.Equal(0, await db.Users.CountAsync());
        }
    }
}
