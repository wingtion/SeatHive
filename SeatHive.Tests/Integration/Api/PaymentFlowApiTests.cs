using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SeatHive.Api.Data;
using SeatHive.Api.Models;
using SeatHive.Shared.Events;

namespace SeatHive.Tests.Integration.Api
{
    // The real API with its outbox, inbox and consumers on the in-memory bus.
    // There is no Worker in this host, so payment results are published by the tests.
    [Collection(ContainersCollection.Name)]
    public class PaymentFlowApiTests
    {
        private readonly ContainersFixture _fixture;
        private readonly ITestHarness _harness;

        public PaymentFlowApiTests(ContainersFixture fixture)
        {
            _fixture = fixture;
            _harness = fixture.Api.Harness;
        }

        private static string ConfirmUrl(int bookingId) => $"/api/Booking/{bookingId}/confirm";

        private static async Task<int> HoldAsync(HttpClient client, int seatId)
        {
            var response = await client.PostAsJsonAsync("/api/Booking/hold", new { seatId });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return body.GetProperty("bookingId").GetInt32();
        }

        // Holds a seat and confirms it; returns the booking while its payment is pending.
        private async Task<(HttpClient Client, Booking Booking)> HoldAndConfirmAsync(object? confirmBody = null)
        {
            var client = await _fixture.Api.CreateUserClientAsync();
            var bookingId = await HoldAsync(client, await _fixture.CreateFreeSeatAsync());

            var response = confirmBody == null
                ? await client.PostAsync(ConfirmUrl(bookingId), null)
                : await client.PostAsJsonAsync(ConfirmUrl(bookingId), confirmBody);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            return (client, await _fixture.ReadBookingAsync(bookingId));
        }

        private static PaymentSucceeded Succeeded(Booking booking) =>
            new(booking.Id, booking.SeatId, booking.UserId, DateTime.UtcNow, booking.PaymentId!.Value);

        // Waits until an event of this type for the booking has come out of the outbox and reached a subscriber.
        private Task<T> DeliveredAsync<T>(int bookingId, Func<T, int> bookingIdOf) where T : class
        {
            return _fixture.Api.WaitForDeliveryAsync<T>(message => bookingIdOf(message) == bookingId);
        }

        private int CountDelivered<T>(int bookingId, Func<T, int> bookingIdOf) where T : class
        {
            return _fixture.Api.CountDelivered<T>(message => bookingIdOf(message) == bookingId);
        }

        [Fact]
        public async Task Hold_ShouldPublishSeatHeld_ThroughTheOutbox()
        {
            var client = await _fixture.Api.CreateUserClientAsync();
            var seatId = await _fixture.CreateFreeSeatAsync();

            var bookingId = await HoldAsync(client, seatId);

            var held = await DeliveredAsync<SeatHeld>(bookingId, e => e.BookingId);
            Assert.Equal(seatId, held.SeatId);
            Assert.Equal(_fixture.Api.Clock.GetUtcNow().UtcDateTime.AddMinutes(5), held.ExpiresAt);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Confirm_ShouldPublishPaymentRequested(bool simulatePaymentFailure)
        {
            var (_, booking) = await HoldAndConfirmAsync(simulatePaymentFailure ? new { simulatePaymentFailure } : null);

            Assert.Equal(BookingStatus.PaymentPending, booking.Status);
            var requested = await DeliveredAsync<PaymentRequested>(booking.Id, e => e.BookingId);
            Assert.Equal(booking.PaymentId, requested.PaymentId);
            Assert.Equal(booking.SeatId, requested.SeatId);
            Assert.Equal(booking.UserId, requested.UserId);
            Assert.Equal(simulatePaymentFailure, requested.ForceFailure);
        }

        [Fact]
        public async Task PaymentSucceeded_ShouldConfirmTheBooking_AndPublishBookingConfirmed()
        {
            var (client, booking) = await HoldAndConfirmAsync();

            await _harness.Bus.Publish(Succeeded(booking));

            var confirmed = await DeliveredAsync<BookingConfirmed>(booking.Id, e => e.BookingId);
            Assert.Equal(booking.PaymentId, confirmed.PaymentId);
            var saved = await _fixture.WaitForBookingStatusAsync(booking.Id, BookingStatus.Confirmed);
            Assert.NotNull(saved.ConfirmedAt);

            // The owner now gets 200 with the confirmed booking.
            var response = await client.PostAsync(ConfirmUrl(booking.Id), null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("confirmed", body.GetProperty("status").GetString());
        }

        [Fact]
        public async Task PaymentFailed_ShouldReturnTheBookingToAHold_ThatCanBeConfirmedAgain()
        {
            var (client, booking) = await HoldAndConfirmAsync();

            await _harness.Bus.Publish(
                new PaymentFailed(booking.Id, booking.SeatId, booking.UserId, DateTime.UtcNow, booking.PaymentId!.Value, "declined"));

            var held = await _fixture.WaitForBookingStatusAsync(booking.Id, BookingStatus.Held);
            Assert.Equal(booking.ExpiresAt, held.ExpiresAt);
            Assert.Equal(0, CountDelivered<BookingConfirmed>(booking.Id, e => e.BookingId));

            var retry = await client.PostAsync(ConfirmUrl(booking.Id), null);
            Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
            Assert.NotEqual(booking.PaymentId, (await _fixture.ReadBookingAsync(booking.Id)).PaymentId);
        }

        [Fact]
        public async Task PaymentSucceeded_ForAnExpiredBooking_ShouldPublishRefundRequested()
        {
            var (_, booking) = await HoldAndConfirmAsync();
            // The payment result never came in time and the sweeper gave the seat up.
            await using (var db = _fixture.CreateContext())
            {
                await db.Bookings.Where(b => b.Id == booking.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BookingStatus.Expired));
            }

            await _harness.Bus.Publish(Succeeded(booking));

            var refund = await DeliveredAsync<RefundRequested>(booking.Id, e => e.BookingId);
            Assert.Equal(booking.PaymentId, refund.PaymentId);
            Assert.Equal("hold_expired", refund.Reason);
            Assert.Equal(BookingStatus.Expired, (await _fixture.ReadBookingAsync(booking.Id)).Status);
            Assert.Equal(0, CountDelivered<BookingConfirmed>(booking.Id, e => e.BookingId));
        }

        [Fact]
        public async Task PaymentSucceeded_AfterTheDemoDataWasReset_ShouldPublishRefundRequested()
        {
            var (_, booking) = await HoldAndConfirmAsync();
            // The reset deletes every booking while this one is waiting for its payment.
            var admin = await _fixture.Api.CreateAdminClientAsync();
            (await admin.PostAsync("/api/Setup/create-data", null)).EnsureSuccessStatusCode();

            await _harness.Bus.Publish(Succeeded(booking));

            // There is no booking to look anything up in, so the refund carries what the payment message said.
            var refund = await _fixture.Api.WaitForDeliveryAsync<RefundRequested>(e => e.PaymentId == booking.PaymentId);
            Assert.Equal((booking.Id, booking.SeatId, booking.UserId), (refund.BookingId, refund.SeatId, refund.UserId));
            Assert.Equal("booking_not_found", refund.Reason);
            Assert.Equal(0, CountDelivered<BookingConfirmed>(booking.Id, e => e.BookingId));
        }

        [Fact]
        public async Task SameMessageDeliveredTwice_ShouldConfirmAndAnnounceOnce()
        {
            var (_, booking) = await HoldAndConfirmAsync();
            var message = Succeeded(booking);
            var messageId = NewId.NextGuid();

            await _harness.Bus.Publish(message, context => context.MessageId = messageId);
            await _harness.Bus.Publish(message, context => context.MessageId = messageId);

            await DeliveredAsync<BookingConfirmed>(booking.Id, e => e.BookingId);

            // The inbox keeps one row for the message and counts how often it arrived.
            // Once it has seen both deliveries, the second one is done as well.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                await using var db = _fixture.CreateContext();
                var inbox = await db.Set<MassTransit.EntityFrameworkCoreIntegration.InboxState>()
                    .AsNoTracking().Where(i => i.MessageId == messageId).ToListAsync();
                if (inbox.Count == 1 && inbox.All(i => i.ReceiveCount >= 2)) break;

                Assert.True(DateTime.UtcNow < deadline, $"Receive counts: [{string.Join(",", inbox.Select(i => i.ReceiveCount))}], expected one row with at least 2.");
                await Task.Delay(20);
            }

            Assert.Equal(1, CountDelivered<BookingConfirmed>(booking.Id, e => e.BookingId));
            Assert.Equal(0, CountDelivered<RefundRequested>(booking.Id, e => e.BookingId));
            Assert.Equal(BookingStatus.Confirmed, (await _fixture.ReadBookingAsync(booking.Id)).Status);
        }

        [Fact]
        public async Task Release_ShouldReturn409_WhileThePaymentIsInProgress()
        {
            var (client, booking) = await HoldAndConfirmAsync();

            var response = await client.PostAsync($"/api/Booking/{booking.Id}/release", null);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("payment_in_progress", body.GetProperty("code").GetString());
        }

        // Publishes one event inside a database transaction of its own scope, then commits or rolls back.
        private async Task<int> PublishInTransactionAsync(bool commit)
        {
            // A booking id no real booking has, so the event can be told apart.
            var marker = -Random.Shared.Next(1, int.MaxValue);

            using var scope = _fixture.Api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var publisher = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

            await using var transaction = await db.Database.BeginTransactionAsync();
            await publisher.Publish(new HoldReleased(marker, 0, 0, DateTime.UtcNow));
            await db.SaveChangesAsync();

            if (commit) await transaction.CommitAsync();
            else await transaction.RollbackAsync();

            return marker;
        }

        [Fact]
        public async Task Outbox_ShouldPublishTheEvent_WhenTheTransactionCommits()
        {
            var marker = await PublishInTransactionAsync(commit: true);

            await _fixture.Api.WaitForDeliveryAsync<HoldReleased>(e => e.BookingId == marker);
        }

        [Fact]
        public async Task Outbox_ShouldNotPublishTheEvent_WhenTheTransactionIsRolledBack()
        {
            var rolledBack = await PublishInTransactionAsync(commit: false);
            // An event committed afterwards: once it has arrived, the outbox has passed the point
            // where the rolled back one would have been delivered.
            var committed = await PublishInTransactionAsync(commit: true);

            await _fixture.Api.WaitForDeliveryAsync<HoldReleased>(e => e.BookingId == committed);

            Assert.Equal(0, _fixture.Api.CountDelivered<HoldReleased>(e => e.BookingId == rolledBack));
            await using var db = _fixture.CreateContext();
            var stored = await db.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>()
                .CountAsync(m => m.Body.Contains(rolledBack.ToString()));
            Assert.Equal(0, stored);
        }
    }
}
