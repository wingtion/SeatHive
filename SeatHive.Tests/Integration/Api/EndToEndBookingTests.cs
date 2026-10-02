using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SeatHive.Api.Models;
using SeatHive.Shared.Events;
using SeatHive.Worker.Data;

namespace SeatHive.Tests.Integration.Api
{
    // The API and the Worker's consumers together on one in-memory bus: nothing is published by hand.
    // Every endpoint is configured as in production: each consumer brings its own inbox, and the one that calls the provider has none.
    [Collection(ContainersCollection.Name)]
    public class EndToEndBookingTests : IAsyncLifetime
    {
        private readonly ContainersFixture _fixture;
        private readonly ApiFactory _api;
        private readonly string _database;

        public EndToEndBookingTests(ContainersFixture fixture)
        {
            _fixture = fixture;
            // A database of its own: every API host delivers what it finds in the outbox table to its own bus,
            // so two hosts on one database would take each other's events. The API creates and migrates it at startup.
            _database = fixture.GetPostgresConnectionString("seathive_e2e");
            _api = new ApiFactory(
                fixture,
                new Dictionary<string, string?> { ["ConnectionStrings__DefaultConnection"] = _database },
                withWorker: true);
        }

        // The Worker creates its own tables at startup; here the test does it for the Worker's part of this host.
        public async Task InitializeAsync()
        {
            var options = new DbContextOptionsBuilder<WorkerDbContext>()
                .UseNpgsql(_database, WorkerDbContext.ConfigureNpgsql)
                .Options;
            await using var db = new WorkerDbContext(options);
            await db.Database.MigrateAsync();
        }

        public async Task DisposeAsync() => await _api.DisposeAsync();

        private async Task<(HttpClient Client, int BookingId)> HoldAsync(ApiFactory? api = null)
        {
            var client = await (api ?? _api).CreateUserClientAsync();
            var seatId = await _fixture.CreateFreeSeatAsync(_database);

            var response = await client.PostAsJsonAsync("/api/Booking/hold", new { seatId });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return (client, body.GetProperty("bookingId").GetInt32());
        }

        [Fact]
        public async Task SuccessfulPayment_ShouldConfirmTheBooking_AndSendTheNotification()
        {
            var (client, bookingId) = await HoldAsync();

            var response = await client.PostAsync($"/api/Booking/{bookingId}/confirm", null);

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var booking = await _fixture.WaitForBookingStatusAsync(bookingId, BookingStatus.Confirmed, _database);

            // The whole story of the booking, in the events a timeline would show.
            await _api.WaitForDeliveryAsync<SeatHeld>(e => e.BookingId == bookingId);
            var requested = await _api.WaitForDeliveryAsync<PaymentRequested>(e => e.BookingId == bookingId);
            var succeeded = await _api.WaitForDeliveryAsync<PaymentSucceeded>(e => e.BookingId == bookingId);
            var confirmed = await _api.WaitForDeliveryAsync<BookingConfirmed>(e => e.BookingId == bookingId);
            var notified = await _api.WaitForDeliveryAsync<NotificationSent>(e => e.BookingId == bookingId);

            Assert.Equal(booking.PaymentId, requested.PaymentId);
            Assert.Equal(booking.PaymentId, succeeded.PaymentId);
            Assert.Equal(booking.PaymentId, confirmed.PaymentId);
            Assert.Equal((booking.SeatId, booking.UserId, "simulated"), (notified.SeatId, notified.UserId, notified.Channel));
            Assert.Equal(0, _api.CountDelivered<RefundRequested>(e => e.BookingId == bookingId));
        }

        [Fact]
        public async Task ProviderCall_ShouldRunWithoutAnOpenDatabaseTransaction()
        {
            // The same host, but the payment takes a second on the test clock, which does not move by itself.
            await using var api = new ApiFactory(
                _fixture,
                new Dictionary<string, string?> { ["ConnectionStrings__DefaultConnection"] = _database },
                withWorker: true,
                paymentDelayMs: 1000);
            var (client, bookingId) = await HoldAsync(api);

            var waiting = api.Clock.NextWaitAsync();
            var response = await client.PostAsync($"/api/Booking/{bookingId}/confirm", null);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            // The payment is now "at the provider". Nothing in this database, API or Worker, holds a transaction for it.
            await waiting.WaitAsync(TimeSpan.FromSeconds(10));
            int openTransactions;
            try
            {
                openTransactions = await _fixture.CountOpenTransactionsAsync(_database);
            }
            finally
            {
                // The provider answers. This also lets the host stop if the test fails.
                api.Clock.Advance(TimeSpan.FromSeconds(1));
            }

            Assert.Equal(0, openTransactions);

            await _fixture.WaitForBookingStatusAsync(bookingId, BookingStatus.Confirmed, _database);
            await api.WaitForDeliveryAsync<NotificationSent>(e => e.BookingId == bookingId);
        }

        [Fact]
        public async Task FailedPayment_ShouldLeaveTheHold_AndConfirmNothing()
        {
            var (client, bookingId) = await HoldAsync();

            var response = await client.PostAsJsonAsync($"/api/Booking/{bookingId}/confirm", new { simulatePaymentFailure = true });

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            await _api.WaitForDeliveryAsync<PaymentFailed>(e => e.BookingId == bookingId);
            await _fixture.WaitForBookingStatusAsync(bookingId, BookingStatus.Held, _database);
            Assert.Equal(0, _api.CountDelivered<BookingConfirmed>(e => e.BookingId == bookingId));
            Assert.Equal(0, _api.CountDelivered<NotificationSent>(e => e.BookingId == bookingId));

            // The owner tries again, this time without the forced failure.
            (await client.PostAsync($"/api/Booking/{bookingId}/confirm", null)).EnsureSuccessStatusCode();
            await _fixture.WaitForBookingStatusAsync(bookingId, BookingStatus.Confirmed, _database);
            await _api.WaitForDeliveryAsync<NotificationSent>(e => e.BookingId == bookingId);
        }

        [Fact]
        public async Task PaymentThatSucceedsAfterTheBookingExpired_ShouldBeRefunded()
        {
            var (_, bookingId) = await HoldAsync();
            // The owner confirmed, but the payment result did not arrive before the sweeper gave the seat up.
            var paymentId = Guid.NewGuid();
            await using (var db = _fixture.CreateContext(_database))
            {
                await db.Bookings.Where(b => b.Id == bookingId).ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, BookingStatus.Expired)
                    .SetProperty(b => b.PaymentId, paymentId));
            }
            var booking = await _fixture.ReadBookingAsync(bookingId, _database);

            await _api.Harness.Bus.Publish(new PaymentSucceeded(bookingId, booking.SeatId, booking.UserId, DateTime.UtcNow, paymentId));

            var requested = await _api.WaitForDeliveryAsync<RefundRequested>(e => e.BookingId == bookingId);
            var completed = await _api.WaitForDeliveryAsync<RefundCompleted>(e => e.BookingId == bookingId);
            Assert.Equal(paymentId, requested.PaymentId);
            Assert.Equal(paymentId, completed.PaymentId);
            Assert.Equal(BookingStatus.Expired, (await _fixture.ReadBookingAsync(bookingId, _database)).Status);
            Assert.Equal(0, _api.CountDelivered<BookingConfirmed>(e => e.BookingId == bookingId));
        }
    }
}
