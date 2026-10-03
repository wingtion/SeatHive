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
    [Collection(TestCollections.EndToEnd)]
    [Trait(TestCategories.Trait, TestCategories.EndToEnd)]
    public class EndToEndBookingTests : IClassFixture<EndToEndHost>
    {
        private readonly EndToEndHost _host;
        private readonly ContainersFixture _fixture;
        private readonly ApiFactory _api;
        private readonly string _database;

        // One host for the whole class; every test works on its own booking.
        public EndToEndBookingTests(EndToEndHost host)
        {
            _host = host;
            _fixture = host.Fixture;
            _api = host.Api;
            _database = host.Database;
        }

        // Users here register and log in for real: this is the whole way through the system.
        private async Task<(HttpClient Client, int BookingId)> HoldAsync(ApiFactory? api = null, string? database = null)
        {
            var client = await (api ?? _api).SignInAsUserAsync();
            var seatId = await _fixture.CreateFreeSeatAsync(database ?? _database);

            var response = await client.PostAsJsonAsync("/api/Booking/hold", new { seatId });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return (client, body.GetProperty("bookingId").GetInt32());
        }

        [Fact]
        public async Task SuccessfulPayment_ShouldConfirmTheBooking_AndSendTheNotification()
        {
            var (client, bookingId) = await HoldAsync();

            var response = await client.PostAsync($"/api/Booking/{bookingId}/confirm", null, TestContext.Current.CancellationToken);

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
            var ct = TestContext.Current.CancellationToken;
            // A host like the one of this class, but the payment takes a second on the test clock, which does not
            // move by itself. It gets a database of its own: the class's host is running, and in a database
            // that only this host uses every open transaction is one of its own.
            var database = _fixture.GetPostgresConnectionString("seathive_e2e_provider_call");
            await ApiWithWorkerHost.MigrateWorkerAsync(database);
            await using var api = _host.Create(database, paymentDelayMs: 1000);
            var (client, bookingId) = await HoldAsync(api, database);

            var waiting = api.Clock.NextWaitAsync();
            var response = await client.PostAsync($"/api/Booking/{bookingId}/confirm", null, ct);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            // The payment is now "at the provider". Nothing in this database, API or Worker, holds a transaction for it.
            await waiting.WaitAsync(TimeSpan.FromSeconds(10), ct);
            int openTransactions;
            try
            {
                openTransactions = await _fixture.CountOpenTransactionsAsync(database);
            }
            finally
            {
                // The provider answers. This also lets the host stop if the test fails.
                api.Clock.Advance(TimeSpan.FromSeconds(1));
            }

            Assert.Equal(0, openTransactions);

            await _fixture.WaitForBookingStatusAsync(bookingId, BookingStatus.Confirmed, database);
            await api.WaitForDeliveryAsync<NotificationSent>(e => e.BookingId == bookingId);
        }

        [Fact]
        public async Task FailedPayment_ShouldLeaveTheHold_AndConfirmNothing()
        {
            var ct = TestContext.Current.CancellationToken;
            var (client, bookingId) = await HoldAsync();

            var response = await client.PostAsJsonAsync($"/api/Booking/{bookingId}/confirm", new { simulatePaymentFailure = true }, cancellationToken: ct);

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            await _api.WaitForDeliveryAsync<PaymentFailed>(e => e.BookingId == bookingId);
            await _fixture.WaitForBookingStatusAsync(bookingId, BookingStatus.Held, _database);
            Assert.Equal(0, _api.CountDelivered<BookingConfirmed>(e => e.BookingId == bookingId));
            Assert.Equal(0, _api.CountDelivered<NotificationSent>(e => e.BookingId == bookingId));

            // The owner tries again, this time without the forced failure.
            (await client.PostAsync($"/api/Booking/{bookingId}/confirm", null, ct)).EnsureSuccessStatusCode();
            await _fixture.WaitForBookingStatusAsync(bookingId, BookingStatus.Confirmed, _database);
            await _api.WaitForDeliveryAsync<NotificationSent>(e => e.BookingId == bookingId);
        }

        [Fact]
        public async Task PaymentThatSucceedsAfterTheBookingExpired_ShouldBeRefunded()
        {
            var ct = TestContext.Current.CancellationToken;
            var (_, bookingId) = await HoldAsync();
            // The owner confirmed, but the payment result did not arrive before the sweeper gave the seat up.
            // The payment was really charged at the provider.
            var paymentId = await _host.ChargeAsync();
            await using (var db = _fixture.CreateContext(_database))
            {
                await db.Bookings.Where(b => b.Id == bookingId).ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, BookingStatus.Expired)
                    .SetProperty(b => b.PaymentId, paymentId), cancellationToken: ct);
            }
            var booking = await _fixture.ReadBookingAsync(bookingId, _database);

            await _api.Harness.Bus.Publish(new PaymentSucceeded(bookingId, booking.SeatId, booking.UserId, DateTime.UtcNow, paymentId), ct);

            var requested = await _api.WaitForDeliveryAsync<RefundRequested>(e => e.BookingId == bookingId);
            var completed = await _api.WaitForDeliveryAsync<RefundCompleted>(e => e.BookingId == bookingId);
            Assert.Equal(paymentId, requested.PaymentId);
            Assert.Equal(paymentId, completed.PaymentId);
            Assert.Equal(BookingStatus.Expired, (await _fixture.ReadBookingAsync(bookingId, _database)).Status);
            Assert.Equal(0, _api.CountDelivered<BookingConfirmed>(e => e.BookingId == bookingId));
        }
    }
}
