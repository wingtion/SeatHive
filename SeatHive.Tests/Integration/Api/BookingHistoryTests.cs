using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SeatHive.Api.Models;
using SeatHive.Shared.Events;
using SeatHive.Worker.Data;

namespace SeatHive.Tests.Integration.Api
{
    // The history of a booking is what really happened to it: every row comes from an event that went through
    // the outbox and the bus. The API and the Worker's consumers run together here, so nothing is made up by hand
    // except where a test says so.
    [Collection(TestCollections.History)]
    [Trait(TestCategories.Trait, TestCategories.EndToEnd)]
    public class BookingHistoryTests : IClassFixture<HistoryHost>
    {
        private readonly HistoryHost _host;
        private readonly ContainersFixture _fixture;
        private readonly ApiFactory _api;
        private readonly string _database;

        // One host for the whole class. Its clock only moves forward, and every test works on its own booking.
        public BookingHistoryTests(HistoryHost host)
        {
            _host = host;
            _fixture = host.Fixture;
            _api = host.Api;
            _database = host.Database;
        }

        private static string HistoryUrl(int bookingId) => $"/api/Booking/{bookingId}/history";

        private DateTime Now() => _api.Clock.GetUtcNow().UtcDateTime;

        private async Task<(HttpClient Client, int BookingId, int SeatId, int UserId)> HoldAsync()
        {
            var client = await _api.CreateUserClientAsync();
            var seatId = await _fixture.CreateFreeSeatAsync(_database);

            var response = await client.PostAsJsonAsync("/api/Booking/hold", new { seatId });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var bookingId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("bookingId").GetInt32();

            var booking = await _fixture.ReadBookingAsync(bookingId, _database);
            return (client, bookingId, seatId, booking.UserId);
        }

        // History rows are written in the background, so they are polled for. Fails after 10 seconds.
        private static async Task<List<JsonElement>> WaitForHistoryAsync(HttpClient client, int bookingId, int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                var response = await client.GetAsync(HistoryUrl(bookingId));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                var page = await response.Content.ReadFromJsonAsync<JsonElement>();
                var items = page.GetProperty("items").EnumerateArray().ToList();
                if (items.Count >= count) return items;

                Assert.True(DateTime.UtcNow < deadline,
                    $"History has [{string.Join(", ", Types(items))}], expected {count} events.");
                await Task.Delay(20);
            }
        }

        private static string[] Types(IEnumerable<JsonElement> items)
        {
            return items.Select(i => i.GetProperty("type").GetString()!).ToArray();
        }

        private async Task<int> CountByEventIdAsync(Guid eventId)
        {
            await using var db = _fixture.CreateContext(_database);
            return await db.BookingEvents.CountAsync(e => e.EventId == eventId);
        }

        private async Task<int> CountByBookingIdAsync(int bookingId)
        {
            await using var db = _fixture.CreateContext(_database);
            return await db.BookingEvents.CountAsync(e => e.BookingId == bookingId);
        }

        private async Task ExecuteAsync(string sql)
        {
            await using var db = _fixture.CreateContext(_database);
            await db.Database.ExecuteSqlRawAsync(sql);
        }

        // ---- What is recorded ----

        [Fact]
        public async Task SuccessfulBooking_ShouldHaveItsWholeStory_InTheOrderItHappened()
        {
            var (client, bookingId, _, _) = await HoldAsync();

            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/Booking/{bookingId}/confirm", null, TestContext.Current.CancellationToken)).StatusCode);

            // The test clock stands still, so every event happened at the same moment:
            // the order then follows the life cycle of a booking.
            var items = await WaitForHistoryAsync(client, bookingId, 5);
            Assert.Equal(
                new[] { "seatHeld", "paymentRequested", "paymentSucceeded", "bookingConfirmed", "notificationSent" },
                Types(items));

            var booking = await _fixture.ReadBookingAsync(bookingId, _database);
            Assert.All(items, item => Assert.Equal(Now(), item.GetProperty("occurredAt").GetDateTime()));
            Assert.Equal(booking.PaymentId, items[1].GetProperty("paymentId").GetGuid());
            Assert.Equal(booking.PaymentId, items[3].GetProperty("paymentId").GetGuid());
            Assert.Equal("simulated", items[4].GetProperty("detail").GetString());

            // Every event has its own id and a sequence number.
            Assert.Equal(5, items.Select(i => i.GetProperty("eventId").GetGuid()).Distinct().Count());
            Assert.Equal(5, items.Select(i => i.GetProperty("sequence").GetInt64()).Distinct().Count());

            // What is simulated says so: the payment and the notification. The hold and the confirmation are real.
            Assert.Equal(
                new[] { false, true, true, false, true },
                items.Select(i => i.GetProperty("simulated").GetBoolean()));
        }

        [Fact]
        public async Task FailedPayment_AndTheRetry_ShouldBothBeInTheHistory()
        {
            var ct = TestContext.Current.CancellationToken;
            var (client, bookingId, _, _) = await HoldAsync();

            (await client.PostAsJsonAsync($"/api/Booking/{bookingId}/confirm", new { simulatePaymentFailure = true }, cancellationToken: ct)).EnsureSuccessStatusCode();
            var failed = await WaitForHistoryAsync(client, bookingId, 3);
            Assert.Equal("forced", failed[2].GetProperty("detail").GetString());
            await _fixture.WaitForBookingStatusAsync(bookingId, BookingStatus.Held, _database);

            // Time passes before the owner tries again.
            _api.Clock.Advance(TimeSpan.FromSeconds(2));
            (await client.PostAsync($"/api/Booking/{bookingId}/confirm", null, ct)).EnsureSuccessStatusCode();

            var items = await WaitForHistoryAsync(client, bookingId, 7);
            Assert.Equal(
                new[] { "seatHeld", "paymentRequested", "paymentFailed", "paymentRequested", "paymentSucceeded", "bookingConfirmed", "notificationSent" },
                Types(items));
            // Two attempts, two payment ids.
            Assert.NotEqual(items[1].GetProperty("paymentId").GetGuid(), items[3].GetProperty("paymentId").GetGuid());
        }

        [Fact]
        public async Task Release_ShouldBeInTheHistory()
        {
            var (client, bookingId, _, _) = await HoldAsync();

            (await client.PostAsync($"/api/Booking/{bookingId}/release", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

            Assert.Equal(new[] { "seatHeld", "holdReleased" }, Types(await WaitForHistoryAsync(client, bookingId, 2)));
        }

        [Fact]
        public async Task Expiry_ShouldBeInTheHistory()
        {
            var (client, bookingId, _, _) = await HoldAsync();

            // The hold runs out and the background sweeper notices.
            _api.Clock.Advance(TimeSpan.FromMinutes(6));

            var items = await WaitForHistoryAsync(client, bookingId, 2);
            Assert.Equal(new[] { "seatHeld", "holdExpired" }, Types(items));
            Assert.True(items[1].GetProperty("occurredAt").GetDateTime() > items[0].GetProperty("occurredAt").GetDateTime());
        }

        [Fact]
        public async Task Refund_ShouldBeInTheHistory()
        {
            var ct = TestContext.Current.CancellationToken;
            var (client, bookingId, seatId, userId) = await HoldAsync();
            // The owner confirmed, but the payment result did not arrive before the sweeper gave the seat up.
            // The payment was really charged at the provider.
            var paymentId = await _host.ChargeAsync();
            await using (var db = _fixture.CreateContext(_database))
            {
                await db.Bookings.Where(b => b.Id == bookingId).ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, BookingStatus.Expired)
                    .SetProperty(b => b.PaymentId, paymentId), cancellationToken: ct);
            }

            await _api.Harness.Bus.Publish(new PaymentSucceeded(bookingId, seatId, userId, Now(), paymentId), ct);

            var items = await WaitForHistoryAsync(client, bookingId, 4);
            Assert.Equal(new[] { "seatHeld", "paymentSucceeded", "refundRequested", "refundCompleted" }, Types(items));
            Assert.Equal("hold_expired", items[2].GetProperty("detail").GetString());
            Assert.Equal(new[] { false, true, true, true }, items.Select(i => i.GetProperty("simulated").GetBoolean()));
        }

        [Fact]
        public async Task RefundThatCouldNotBeMade_ShouldBeInTheHistory_AsFailed()
        {
            var ct = TestContext.Current.CancellationToken;
            var (client, bookingId, seatId, userId) = await HoldAsync();
            // A payment result for a payment the provider knows nothing about: there is no charge to give back.
            var paymentId = Guid.NewGuid();
            await using (var db = _fixture.CreateContext(_database))
            {
                await db.Bookings.Where(b => b.Id == bookingId).ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, BookingStatus.Expired)
                    .SetProperty(b => b.PaymentId, paymentId), cancellationToken: ct);
            }

            await _api.Harness.Bus.Publish(new PaymentSucceeded(bookingId, seatId, userId, Now(), paymentId), ct);

            // The history says what happened: the refund was asked for and could not be made. It does not say "completed".
            var items = await WaitForHistoryAsync(client, bookingId, 4);
            Assert.Equal(new[] { "seatHeld", "paymentSucceeded", "refundRequested", "refundFailed" }, Types(items));
            Assert.Equal("charge_not_found", items[3].GetProperty("detail").GetString());
            Assert.True(items[3].GetProperty("simulated").GetBoolean());
            Assert.Equal(paymentId, items[3].GetProperty("paymentId").GetGuid());
            Assert.Equal(0, _api.CountDelivered<RefundCompleted>(e => e.BookingId == bookingId));
        }

        [Fact]
        public async Task RolledBackChange_ShouldLeaveNothingInTheHistory()
        {
            var ct = TestContext.Current.CancellationToken;
            var (client, bookingId, seatId, userId) = await HoldAsync();
            await WaitForHistoryAsync(client, bookingId, 1);

            // An event stored in the outbox by a transaction that is then rolled back...
            using (var scope = _api.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SeatHive.Api.Data.AppDbContext>();
                var publisher = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await publisher.Publish(new HoldReleased(bookingId, seatId, userId, Now()), ct);
                await db.SaveChangesAsync(ct);
                await transaction.RollbackAsync(ct);
            }

            // ...and one that really happened afterwards. Once that one is recorded, the first would have been too.
            (await client.PostAsync($"/api/Booking/{bookingId}/confirm", null, ct)).EnsureSuccessStatusCode();
            var items = await WaitForHistoryAsync(client, bookingId, 5);

            Assert.DoesNotContain("holdReleased", Types(items));
        }

        // ---- Order ----

        [Fact]
        public async Task History_ShouldBeOrderedByWhenAnEventHappened_NotByWhenItWasRecorded()
        {
            var (client, bookingId, seatId, userId) = await HoldAsync();
            await WaitForHistoryAsync(client, bookingId, 1);

            // Recorded last, but it happened ten minutes before the hold.
            await _api.Harness.Bus.Publish(new PaymentFailed(bookingId, seatId, userId, Now().AddMinutes(-10), Guid.NewGuid(), "late-arrival"), TestContext.Current.CancellationToken);

            var items = await WaitForHistoryAsync(client, bookingId, 2);
            Assert.Equal(new[] { "paymentFailed", "seatHeld" }, Types(items));
            Assert.True(items[0].GetProperty("sequence").GetInt64() > items[1].GetProperty("sequence").GetInt64());
        }

        [Fact]
        public async Task EventsOfTheSameKindAtTheSameMoment_ShouldBeOrderedBySequence()
        {
            var ct = TestContext.Current.CancellationToken;
            var (client, bookingId, seatId, userId) = await HoldAsync();
            var moment = Now().AddMinutes(1);

            await _api.Harness.Bus.Publish(new PaymentFailed(bookingId, seatId, userId, moment, Guid.NewGuid(), "first"), ct);
            await WaitForHistoryAsync(client, bookingId, 2);
            await _api.Harness.Bus.Publish(new PaymentFailed(bookingId, seatId, userId, moment, Guid.NewGuid(), "second"), ct);

            var items = await WaitForHistoryAsync(client, bookingId, 3);
            Assert.Equal(new[] { "first", "second" }, items.Skip(1).Select(i => i.GetProperty("detail").GetString()));
            Assert.True(items[2].GetProperty("sequence").GetInt64() > items[1].GetProperty("sequence").GetInt64());
        }

        [Fact]
        public async Task History_ShouldBePaged()
        {
            var ct = TestContext.Current.CancellationToken;
            var (client, bookingId, _, _) = await HoldAsync();
            (await client.PostAsync($"/api/Booking/{bookingId}/confirm", null, ct)).EnsureSuccessStatusCode();
            await WaitForHistoryAsync(client, bookingId, 5);

            var second = await (await client.GetAsync($"{HistoryUrl(bookingId)}?page=2&pageSize=2", ct)).Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var tooLarge = await client.GetAsync($"{HistoryUrl(bookingId)}?pageSize=101", ct);

            Assert.Equal(new[] { "paymentSucceeded", "bookingConfirmed" }, Types(second.GetProperty("items").EnumerateArray()));
            Assert.Equal(5, second.GetProperty("totalCount").GetInt32());
            await ProblemAssert.HasCodeAsync(tooLarge, HttpStatusCode.BadRequest, "validation_failed");
        }

        // ---- Exactly once ----

        [Fact]
        public async Task SameEventDeliveredTwice_ShouldBeRecordedOnce()
        {
            var ct = TestContext.Current.CancellationToken;
            var (client, bookingId, seatId, userId) = await HoldAsync();
            var message = new HoldReleased(bookingId, seatId, userId, Now());
            var messageId = NewId.NextGuid();

            await _api.Harness.Bus.Publish(message, context => context.MessageId = messageId, cancellationToken: ct);
            await _api.Harness.Bus.Publish(message, context => context.MessageId = messageId, cancellationToken: ct);
            await WaitForHistoryAsync(client, bookingId, 2);

            // A later event of the booking: once it is recorded, the second delivery has been dealt with as well.
            await _api.Harness.Bus.Publish(new PaymentFailed(bookingId, seatId, userId, Now().AddMinutes(1), Guid.NewGuid(), "marker"), ct);
            await WaitForHistoryAsync(client, bookingId, 3);

            Assert.Equal(1, await CountByEventIdAsync(messageId));
        }

        [Fact]
        public async Task SameEventDeliveredAgain_AfterTheInboxForgotIt_ShouldStillBeRecordedOnce()
        {
            var ct = TestContext.Current.CancellationToken;
            var (client, bookingId, seatId, userId) = await HoldAsync();
            var message = new HoldReleased(bookingId, seatId, userId, Now());
            var messageId = NewId.NextGuid();
            await _api.Harness.Bus.Publish(message, context => context.MessageId = messageId, cancellationToken: ct);
            await WaitForHistoryAsync(client, bookingId, 2);

            // The inbox only remembers a message for a while. After that, the unique event id is what is left.
            await using (var db = _fixture.CreateContext(_database))
            {
                var forgotten = await db.Set<MassTransit.EntityFrameworkCoreIntegration.InboxState>()
                    .Where(i => i.MessageId == messageId).ExecuteDeleteAsync(cancellationToken: ct);
                Assert.Equal(1, forgotten);
            }

            await _api.Harness.Bus.Publish(message, context => context.MessageId = messageId, cancellationToken: ct);
            await _api.Harness.Bus.Publish(new PaymentFailed(bookingId, seatId, userId, Now().AddMinutes(1), Guid.NewGuid(), "marker"), ct);
            await WaitForHistoryAsync(client, bookingId, 3);

            Assert.Equal(1, await CountByEventIdAsync(messageId));
        }

        [Fact]
        public async Task EventId_ShouldBeUnique_InTheDatabase()
        {
            var eventId = Guid.NewGuid();
            const string insert =
                "INSERT INTO \"BookingEvents\" (\"EventId\", \"BookingId\", \"SeatId\", \"UserId\", \"Type\", \"OccurredAt\") " +
                "VALUES ({0}, 1, 1, 1, 'SeatHeld', now())";

            await using var db = _fixture.CreateContext(_database);
            await db.Database.ExecuteSqlRawAsync(insert, eventId);

            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync(insert, eventId));
            Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, error.SqlState);
        }

        [Fact]
        public async Task HistoryRow_ShouldBeWrittenInTheSameTransactionAsTheInboxRecord()
        {
            var ct = TestContext.Current.CancellationToken;
            var (client, bookingId, seatId, userId) = await HoldAsync();
            await WaitForHistoryAsync(client, bookingId, 1);
            var messageId = NewId.NextGuid();
            var message = new HoldReleased(bookingId, seatId, userId, Now());

            // The inbox marks a message as consumed in the transaction the consumer worked in.
            // Here that last step fails, after the consumer has already written its row.
            await ExecuteAsync(
                """
                CREATE OR REPLACE FUNCTION fail_inbox_consumed() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION 'test: marking the message as consumed fails'; END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER fail_inbox_consumed BEFORE UPDATE ON "InboxState"
                FOR EACH ROW WHEN (NEW."Consumed" IS NOT NULL) EXECUTE FUNCTION fail_inbox_consumed();
                """);
            try
            {
                await _api.Harness.Bus.Publish(message, context => context.MessageId = messageId, cancellationToken: ct);

                // The fault is announced after the last retry, more than a second and a half later. It is polled for
                // with a deadline of its own: the harness gives up waiting as soon as the bus is quiet for a moment,
                // and the pause before the last retry is long enough for that.
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (!_api.Harness.Published.Select<Fault<HoldReleased>>(f => f.Context.Message.Message.BookingId == bookingId, ct).Any())
                {
                    Assert.True(DateTime.UtcNow < deadline, "The consumer was expected to fail.");
                    await Task.Delay(50, ct);
                }

                // The row went with the transaction: nothing of the event is left.
                Assert.Equal(0, await CountByEventIdAsync(messageId));
                await using var db = _fixture.CreateContext(_database);
                Assert.Equal(0, await db.Set<MassTransit.EntityFrameworkCoreIntegration.InboxState>()
                    .CountAsync(i => i.MessageId == messageId && i.Consumed != null, cancellationToken: ct));
            }
            finally
            {
                await ExecuteAsync("DROP TRIGGER fail_inbox_consumed ON \"InboxState\"; DROP FUNCTION fail_inbox_consumed();");
            }

            // Delivered again when the database works: recorded, once.
            await _api.Harness.Bus.Publish(message, context => context.MessageId = messageId, cancellationToken: ct);
            await WaitForHistoryAsync(client, bookingId, 2);
            Assert.Equal(1, await CountByEventIdAsync(messageId));
        }

        // ---- Who may read it ----

        [Fact]
        public async Task History_ShouldReturn403_ForAnotherUser_AndForAdmin()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, bookingId, _, _) = await HoldAsync();
            var other = await _api.CreateUserClientAsync();
            var admin = await _api.CreateAdminClientAsync();

            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(HistoryUrl(bookingId), ct)).StatusCode);
            await ProblemAssert.HasCodeAsync(await other.GetAsync(HistoryUrl(bookingId), ct), HttpStatusCode.Forbidden, "not_hold_owner");
            await ProblemAssert.HasCodeAsync(await admin.GetAsync(HistoryUrl(bookingId), ct), HttpStatusCode.Forbidden, "not_hold_owner");
        }

        [Fact]
        public async Task History_ShouldReturn401_WithoutAToken()
        {
            var response = await _api.CreateClient().GetAsync(HistoryUrl(1), TestContext.Current.CancellationToken);

            await ProblemAssert.HasCodeAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
        }

        [Fact]
        public async Task History_ShouldReturn404_WhenTheBookingDoesNotExist()
        {
            var client = await _api.CreateUserClientAsync();

            var response = await client.GetAsync(HistoryUrl(int.MaxValue), TestContext.Current.CancellationToken);

            await ProblemAssert.HasCodeAsync(response, HttpStatusCode.NotFound, "booking_not_found");
        }

        // ---- Reset ----

        [Fact]
        public async Task Reset_ShouldDeleteTheHistory_AndAnnounceItself()
        {
            var ct = TestContext.Current.CancellationToken;
            var (client, bookingId, _, _) = await HoldAsync();
            await WaitForHistoryAsync(client, bookingId, 1);
            var admin = await _api.CreateAdminClientAsync();

            (await admin.PostAsync("/api/Setup/create-data", null, ct)).EnsureSuccessStatusCode();

            Assert.Equal(0, await CountByBookingIdAsync(bookingId));
            await ProblemAssert.HasCodeAsync(await client.GetAsync(HistoryUrl(bookingId), ct), HttpStatusCode.NotFound, "booking_not_found");
            // The reset goes through the outbox like every other change, so subscribers hear about it after the commit.
            await _api.WaitForDeliveryAsync<DemoDataReset>(_ => true);
        }
    }
}
