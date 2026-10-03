using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SeatHive.Worker.Data
{
    // Used only by the EF Core tools ("dotnet ef migrations add"), which need a context without starting the Worker.
    // The connection string is never opened for that.
    public class WorkerDbContextFactory : IDesignTimeDbContextFactory<WorkerDbContext>
    {
        public WorkerDbContext CreateDbContext(string[] args)
        {
            var options = new DbContextOptionsBuilder<WorkerDbContext>()
                .UseNpgsql("Host=localhost;Database=seathivedb", WorkerDbContext.ConfigureNpgsql)
                .Options;

            return new WorkerDbContext(options);
        }
    }
}
