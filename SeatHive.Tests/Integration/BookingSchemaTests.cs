using Microsoft.EntityFrameworkCore;
using Npgsql;
using SeatHive.Api.Models;

namespace SeatHive.Tests.Integration
{
    // These tests talk to the database directly, with no lock and no service in between:
    // the guarantees below must hold in PostgreSQL itself.
    [Collection(TestCollections.HoldService)]
    [Trait(TestCategories.Trait, TestCategories.Integration)]
    public class BookingSchemaTests
    {
        private readonly ContainersFixture _fixture;

        public BookingSchemaTests(ContainersFixture fixture)
        {
            _fixture = fixture;
        }

        private async Task InsertBookingAsync(int seatId, int userId, string status)
        {
            await using var db = _fixture.CreateContext();
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Bookings\" (\"SeatId\", \"UserId\", \"Status\", \"CreatedAt\") VALUES ({0}, {1}, {2}, now())",
                seatId, userId, status);
        }

        [Theory]
        [InlineData("Confirmed", "Confirmed")]
        [InlineData("Confirmed", "Held")]
        [InlineData("Held", "Confirmed")]
        [InlineData("Held", "Held")]
        [InlineData("PaymentPending", "Held")]
        [InlineData("PaymentPending", "Confirmed")]
        [InlineData("Held", "PaymentPending")]
        public async Task SecondActiveBooking_ShouldBeRejectedByTheDatabase(string first, string second)
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var users = await _fixture.CreateUsersAsync(2);
            await InsertBookingAsync(seatId, users[0], first);

            var error = await Assert.ThrowsAsync<PostgresException>(() => InsertBookingAsync(seatId, users[1], second));

            Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        }

        [Fact]
        public async Task ExpiredAndReleasedBookings_ShouldNotBlockANewBooking()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var users = await _fixture.CreateUsersAsync(3);
            await InsertBookingAsync(seatId, users[0], "Expired");
            await InsertBookingAsync(seatId, users[1], "Released");

            await InsertBookingAsync(seatId, users[2], "Confirmed");
        }

        [Fact]
        public async Task Booking_ShouldBeRejected_ForUnknownUser()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();

            var error = await Assert.ThrowsAsync<PostgresException>(() => InsertBookingAsync(seatId, int.MaxValue, "Confirmed"));

            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        }

        [Fact]
        public async Task Seat_ShouldBeUnique_PerEventSectionRowAndNumber()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            await using var db = _fixture.CreateContext();
            var existing = await db.Seats.AsNoTracking().SingleAsync(s => s.Id == seatId);

            db.Seats.Add(new Seat
            {
                EventId = existing.EventId,
                Section = existing.Section,
                Row = existing.Row,
                SeatNumber = existing.SeatNumber
            });
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

            var postgresError = Assert.IsType<PostgresException>(error.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresError.SqlState);
        }
    }
}
