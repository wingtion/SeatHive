using MassTransit;
using Microsoft.EntityFrameworkCore;
using SeatHive.Api.Models;
using Event = SeatHive.Api.Models.Event;

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
        public DbSet<Booking> Bookings { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Emails are stored lowercase, so this also blocks duplicates that differ only by casing.
            modelBuilder.Entity<User>().HasIndex(u => u.Email).IsUnique();

            modelBuilder.Entity<Seat>()
                .HasIndex(s => new { s.EventId, s.Section, s.Row, s.SeatNumber })
                .IsUnique();

            modelBuilder.Entity<Booking>(booking =>
            {
                booking.Property(b => b.Status).HasConversion<string>();

                // The database guarantee: a seat has at most one active (Held, PaymentPending or Confirmed) booking.
                // Expired and Released bookings stay as history and do not block the seat.
                booking.HasIndex(b => b.SeatId)
                    .IsUnique()
                    .HasFilter("\"Status\" IN ('Held', 'PaymentPending', 'Confirmed')");

                booking.HasOne(b => b.Seat)
                    .WithMany(s => s.Bookings)
                    .HasForeignKey(b => b.SeatId)
                    .OnDelete(DeleteBehavior.Cascade);

                booking.HasOne(b => b.User)
                    .WithMany()
                    .HasForeignKey(b => b.UserId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            // MassTransit transactional outbox and inbox: events are stored in the same transaction
            // as the booking change, and a message delivered twice is consumed once.
            modelBuilder.AddInboxStateEntity();
            modelBuilder.AddOutboxMessageEntity();
            modelBuilder.AddOutboxStateEntity();
        }
    }
}
