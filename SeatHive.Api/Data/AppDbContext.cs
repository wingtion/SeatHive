using Microsoft.EntityFrameworkCore;
using SeatHive.Api.Models;

namespace SeatHive.Api.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<Event> Events { get; set; }
        public DbSet<Seat> Seats { get; set; }
        public DbSet<User> Users { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Emails are stored lowercase, so this also blocks duplicates that differ only by casing.
            modelBuilder.Entity<User>().HasIndex(u => u.Email).IsUnique();
        }
    }
}
