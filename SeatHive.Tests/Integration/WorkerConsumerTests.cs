using System.Collections.Concurrent;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using SeatHive.Shared.Events;
using SeatHive.Tests.Integration.Api;
using SeatHive.Worker;
using SeatHive.Worker.Consumers;
using SeatHive.Worker.Data;
using SeatHive.Worker.Payments;

namespace SeatHive.Tests.Integration
{
    // The Worker's consumers with its own inbox and outbox, on an in-memory bus and the real database.
    // Payments take no time here unless a test says otherwise; whether they fail at random is set per test.
    [Collection(TestCollections.Worker)]
    [Trait(TestCategories.Trait, TestCategories.Integration)]
    public class WorkerConsumerTests : IClassFixture<WorkerConsumerTests.DefaultWorker>
    {
        private static int _nextBookingId = 1_000_000;

        private readonly ContainersFixture _fixture;
        private readonly ServiceProvider _defaultWorker;

        // A Worker whose payments never fail at random and take no time, started once for the class.
        // Tests that need other settings start their own. Every test uses ids of its own, so sharing is safe.
        public sealed class DefaultWorker : IAsyncLifetime
        {
            private readonly ContainersFixture _fixture;

            public DefaultWorker(ContainersFixture fixture)
            {
                _fixture = fixture;
            }

            public ServiceProvider Services { get; private set; } = null!;

            public async ValueTask InitializeAsync() => Services = await StartWorkerAsync(_fixture, failureRate: 0);

            public async ValueTask DisposeAsync() => await Services.DisposeAsync();
        }

        public WorkerConsumerTests(ContainersFixture fixture, DefaultWorker defaultWorker)
        {
            _fixture = fixture;
            _defaultWorker = defaultWorker.Services;
        }

        // How often the provider finished a charge call, per idempotency key.
        private sealed class ChargeCalls
        {
            public ConcurrentDictionary<Guid, int> Finished { get; } = new();
        }

        private sealed class CountingProvider : ISimulatedPaymentProvider
        {
            private readonly ISimulatedPaymentProvider _inner;
            private readonly ChargeCalls _calls;

            public CountingProvider(ISimulatedPaymentProvider inner, ChargeCalls calls)
            {
                _inner = inner;
                _calls = calls;
            }

            public async Task<ChargeResult> ChargeAsync(Guid idempotencyKey, bool forceFailure, CancellationToken cancellationToken = default)
            {
                var result = await _inner.ChargeAsync(idempotencyKey, forceFailure, cancellationToken);
                _calls.Finished.AddOrUpdate(idempotencyKey, 1, (_, count) => count + 1);
                return result;
            }

            public Task<RefundOutcome> RefundAsync(Guid idempotencyKey, CancellationToken cancellationToken = default)
            {
                return _inner.RefundAsync(idempotencyKey, cancellationToken);
            }

            public Task<int> DeleteExpiredChargesAsync(CancellationToken cancellationToken = default)
            {
                return _inner.DeleteExpiredChargesAsync(cancellationToken);
            }
        }

        // connectionString picks another database on the same server; the default is the shared test database.
        private static async Task<ServiceProvider> StartWorkerAsync(
            ContainersFixture fixture,
            double failureRate, TimeProvider? clock = null, int delayMs = 0, string? connectionString = null)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(clock ?? TimeProvider.System);
            services.Configure<PaymentOptions>(o =>
            {
                o.FailureRate = failureRate;
                o.MinDelayMs = delayMs;
                o.MaxDelayMs = delayMs;
            });
            services.AddDbContext<WorkerDbContext>(o =>
                o.UseNpgsql(connectionString ?? fixture.GetPostgresConnectionString(), WorkerDbContext.ConfigureNpgsql));

            // The real provider, wrapped only to count its calls.
            services.AddSingleton<ChargeCalls>();
            services.AddScoped<SimulatedPaymentProvider>();
            services.AddScoped<ISimulatedPaymentProvider>(provider => new CountingProvider(
                provider.GetRequiredService<SimulatedPaymentProvider>(), provider.GetRequiredService<ChargeCalls>()));

            services.AddMassTransitTestHarness(x =>
            {
                x.AddWorkerConsumers();
                x.AddWorkerOutbox();

                // What the Worker publishes comes out of its outbox; these subscribers make it visible to the tests.
                x.AddEventRecorders();
            });

            var provider = services.BuildServiceProvider();

            // The Worker applies its own migrations at startup; so does this host.
            await using (var scope = provider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<WorkerDbContext>().Database.MigrateAsync();
            }

            await provider.GetRequiredService<ITestHarness>().Start();
            return provider;
        }

        // Runs a query on the Worker's database in a scope of its own.
        private static async Task<T> WithDbAsync<T>(ServiceProvider worker, Func<WorkerDbContext, Task<T>> query)
        {
            await using var scope = worker.CreateAsyncScope();
            return await query(scope.ServiceProvider.GetRequiredService<WorkerDbContext>());
        }

        // Things finish in the background, so they are polled for. Fails after 10 seconds.
        private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!await condition())
            {
                Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting until {what}.");
                await Task.Delay(20);
            }
        }

        private static Task<T> DeliveredAsync<T>(ServiceProvider worker, int bookingId, Func<T, int> bookingIdOf) where T : class
        {
            return worker.GetRequiredService<EventLog>().WaitForAsync<T>(message => bookingIdOf(message) == bookingId);
        }

        private static int Count<T>(ServiceProvider worker, int bookingId, Func<T, int> bookingIdOf) where T : class
        {
            return worker.GetRequiredService<EventLog>().Of<T>(message => bookingIdOf(message) == bookingId).Count;
        }

        // These tests have no bookings in the database; the ids only need to be unique.
        private static PaymentRequested NewPaymentRequest(bool forceFailure = false)
        {
            var bookingId = Interlocked.Increment(ref _nextBookingId);
            return new PaymentRequested(bookingId, SeatId: 7, UserId: 9, DateTime.UtcNow, Guid.NewGuid(), forceFailure);
        }

        [Fact]
        public async Task PaymentRequested_ShouldPublishPaymentSucceeded_WhenPaymentsNeverFail()
        {
            var worker = _defaultWorker;
            var harness = worker.GetRequiredService<ITestHarness>();
            var request = NewPaymentRequest();

            await harness.Bus.Publish(request, TestContext.Current.CancellationToken);

            var succeeded = await DeliveredAsync<PaymentSucceeded>(worker, request.BookingId, e => e.BookingId);
            Assert.Equal((request.SeatId, request.UserId, request.PaymentId), (succeeded.SeatId, succeeded.UserId, succeeded.PaymentId));
            Assert.Equal(0, Count<PaymentFailed>(worker, request.BookingId, e => e.BookingId));
        }

        [Fact]
        public async Task PaymentRequested_ShouldPublishPaymentFailed_WhenFailureIsForced()
        {
            // Random failures are off, so only the flag on the request can make it fail.
            var worker = _defaultWorker;
            var harness = worker.GetRequiredService<ITestHarness>();
            var request = NewPaymentRequest(forceFailure: true);

            await harness.Bus.Publish(request, TestContext.Current.CancellationToken);

            var failed = await DeliveredAsync<PaymentFailed>(worker, request.BookingId, e => e.BookingId);
            Assert.Equal((request.SeatId, request.UserId, request.PaymentId), (failed.SeatId, failed.UserId, failed.PaymentId));
            Assert.False(string.IsNullOrWhiteSpace(failed.Reason));
            Assert.Equal(0, Count<PaymentSucceeded>(worker, request.BookingId, e => e.BookingId));
        }

        [Fact]
        public async Task PaymentRequested_ShouldPublishPaymentFailed_WhenTheFailureRateIsOne()
        {
            await using var worker = await StartWorkerAsync(_fixture, failureRate: 1);
            var harness = worker.GetRequiredService<ITestHarness>();
            var request = NewPaymentRequest();

            await harness.Bus.Publish(request, TestContext.Current.CancellationToken);

            await DeliveredAsync<PaymentFailed>(worker, request.BookingId, e => e.BookingId);
            Assert.Equal(0, Count<PaymentSucceeded>(worker, request.BookingId, e => e.BookingId));
        }

        [Fact]
        public async Task ProviderCall_ShouldRunWithoutAnOpenDatabaseTransaction()
        {
            var ct = TestContext.Current.CancellationToken;
            // A database of its own, so every other session in it belongs to this Worker.
            var connectionString = _fixture.GetPostgresConnectionString("seathive_worker_tx");
            var clock = new GatedClock();
            await using var worker = await StartWorkerAsync(_fixture, failureRate: 0, clock, delayMs: 1000, connectionString);
            var harness = worker.GetRequiredService<ITestHarness>();
            var request = NewPaymentRequest();

            var waiting = clock.NextWaitAsync();
            await harness.Bus.Publish(request, ct);

            // The payment is now "at the provider": the consumer waits on the clock, which does not move.
            await waiting.WaitAsync(TimeSpan.FromSeconds(10), ct);
            int openTransactions;
            try
            {
                openTransactions = await _fixture.CountOpenTransactionsAsync(connectionString);
            }
            finally
            {
                // The provider answers. This also lets the Worker stop if the test fails.
                clock.Advance(TimeSpan.FromSeconds(1));
            }

            Assert.Equal(0, openTransactions);

            // The result is recorded and published.
            await DeliveredAsync<PaymentSucceeded>(worker, request.BookingId, e => e.BookingId);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task SamePaymentRequestDeliveredTwice_ShouldChargeOnce_AndProduceOneResult(bool sameMessageId)
        {
            var ct = TestContext.Current.CancellationToken;
            var worker = _defaultWorker;
            var harness = worker.GetRequiredService<ITestHarness>();
            var calls = worker.GetRequiredService<ChargeCalls>();
            var request = NewPaymentRequest();
            var messageId = NewId.NextGuid();

            // The same request arrives twice: redelivered by the broker (same message id)
            // or sent again by the publisher (a new message id).
            await harness.Bus.Publish(request, context => context.MessageId = messageId, cancellationToken: ct);
            await harness.Bus.Publish(request, context => context.MessageId = sameMessageId ? messageId : NewId.NextGuid(), cancellationToken: ct);

            await DeliveredAsync<PaymentSucceeded>(worker, request.BookingId, e => e.BookingId);

            // Both deliveries went to the provider...
            await WaitUntilAsync(
                () => Task.FromResult(calls.Finished.GetValueOrDefault(request.PaymentId) >= 2),
                "the provider was asked twice");
            // ...and both outcomes reached the second step, whose inbox counts how often its message arrived.
            await WaitUntilAsync(
                async () =>
                {
                    var inbox = await WithDbAsync(worker, db => db.Set<MassTransit.EntityFrameworkCoreIntegration.InboxState>()
                        .AsNoTracking().Where(i => i.MessageId == request.PaymentId).ToListAsync());
                    return inbox.Count > 0 && inbox.All(i => i.ReceiveCount >= 2);
                },
                "both outcomes were received");

            // One charge at the provider, one result for the API.
            Assert.Equal(1, await WithDbAsync(worker, db => db.SimulatedCharges.CountAsync(c => c.IdempotencyKey == request.PaymentId)));
            Assert.Equal(1, Count<PaymentSucceeded>(worker, request.BookingId, e => e.BookingId));
            Assert.Equal(0, Count<PaymentFailed>(worker, request.BookingId, e => e.BookingId));
        }

        [Fact]
        public async Task Provider_ShouldReturnTheFirstResult_WhenAskedAgainWithTheSameKey()
        {
            var worker = _defaultWorker;
            var key = Guid.NewGuid();

            async Task<ChargeResult> ChargeAsync(Guid idempotencyKey, bool forceFailure)
            {
                await using var scope = worker.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<SimulatedPaymentProvider>().ChargeAsync(idempotencyKey, forceFailure);
            }

            var first = await ChargeAsync(key, forceFailure: true);
            // Without the key this call would succeed: failures are off and nothing is forced.
            var second = await ChargeAsync(key, forceFailure: false);
            var otherKey = await ChargeAsync(Guid.NewGuid(), forceFailure: false);

            Assert.False(first.Succeeded);
            Assert.Equal(first, second);
            Assert.True(otherKey.Succeeded);
            Assert.Equal(1, await WithDbAsync(worker, db => db.SimulatedCharges.CountAsync(c => c.IdempotencyKey == key)));
        }

        [Fact]
        public async Task Provider_ShouldChargeOnce_WhenTheSameKeyArrivesTwiceAtTheSameTime()
        {
            var worker = _defaultWorker;

            for (var round = 0; round < 10; round++)
            {
                var key = Guid.NewGuid();

                // The two calls would end differently on their own, so equal results mean one charge.
                var results = await Task.WhenAll(new[] { true, false }.Select(forceFailure => Task.Run(async () =>
                {
                    await using var scope = worker.CreateAsyncScope();
                    return await scope.ServiceProvider.GetRequiredService<SimulatedPaymentProvider>().ChargeAsync(key, forceFailure);
                })));

                Assert.Equal(results[0], results[1]);
                Assert.Equal(1, await WithDbAsync(worker, db => db.SimulatedCharges.CountAsync(c => c.IdempotencyKey == key)));
            }
        }

        [Fact]
        public async Task RefundRequested_ShouldPublishRefundCompleted()
        {
            var worker = _defaultWorker;
            var harness = worker.GetRequiredService<ITestHarness>();
            var bookingId = Interlocked.Increment(ref _nextBookingId);
            var paymentId = await ChargeAsync(worker);

            await harness.Bus.Publish(new RefundRequested(bookingId, 7, 9, DateTime.UtcNow, paymentId, "hold_expired"), TestContext.Current.CancellationToken);

            var refunded = await DeliveredAsync<RefundCompleted>(worker, bookingId, e => e.BookingId);
            Assert.Equal((7, 9, paymentId), (refunded.SeatId, refunded.UserId, refunded.PaymentId));
            Assert.NotNull((await ReadChargeAsync(worker, paymentId)).RefundedAt);
            Assert.Equal(0, Count<RefundFailed>(worker, bookingId, e => e.BookingId));
        }

        // Charges a payment at the provider, as a payment request does, and returns its id.
        private static async Task<Guid> ChargeAsync(ServiceProvider worker, bool forceFailure = false)
        {
            var paymentId = Guid.NewGuid();

            await using var scope = worker.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SimulatedPaymentProvider>().ChargeAsync(paymentId, forceFailure);

            return paymentId;
        }

        private static Task<SimulatedCharge> ReadChargeAsync(ServiceProvider worker, Guid paymentId)
        {
            return WithDbAsync(worker, db => db.SimulatedCharges.AsNoTracking().SingleAsync(c => c.IdempotencyKey == paymentId));
        }

        // Waits until the Worker is done with a message, for messages that are expected to produce nothing.
        private static Task ConsumedAsync(ServiceProvider worker, Guid messageId)
        {
            return WaitUntilAsync(
                () => WithDbAsync(worker, db => db.Set<MassTransit.EntityFrameworkCoreIntegration.InboxState>()
                    .AnyAsync(i => i.MessageId == messageId && i.Consumed != null)),
                $"message {messageId} was consumed");
        }

        [Fact]
        public async Task RefundRequested_WithoutACharge_ShouldPublishRefundFailed_AndNotPretendToRefund()
        {
            var worker = _defaultWorker;
            var harness = worker.GetRequiredService<ITestHarness>();
            var bookingId = Interlocked.Increment(ref _nextBookingId);
            // A payment the provider has never charged (or has forgotten).
            var paymentId = Guid.NewGuid();

            await harness.Bus.Publish(new RefundRequested(bookingId, 7, 9, DateTime.UtcNow, paymentId, "hold_expired"), TestContext.Current.CancellationToken);

            var failed = await DeliveredAsync<RefundFailed>(worker, bookingId, e => e.BookingId);
            Assert.Equal((7, 9, paymentId, "charge_not_found"), (failed.SeatId, failed.UserId, failed.PaymentId, failed.Reason));
            Assert.Equal(0, Count<RefundCompleted>(worker, bookingId, e => e.BookingId));
        }

        [Fact]
        public async Task RefundRequested_ForAChargeThatFailed_ShouldPublishRefundFailed()
        {
            var worker = _defaultWorker;
            var harness = worker.GetRequiredService<ITestHarness>();
            var bookingId = Interlocked.Increment(ref _nextBookingId);
            // The charge failed, so no money was taken and there is nothing to give back.
            var paymentId = await ChargeAsync(worker, forceFailure: true);

            await harness.Bus.Publish(new RefundRequested(bookingId, 7, 9, DateTime.UtcNow, paymentId, "hold_expired"), TestContext.Current.CancellationToken);

            var failed = await DeliveredAsync<RefundFailed>(worker, bookingId, e => e.BookingId);
            Assert.Equal("charge_not_successful", failed.Reason);
            Assert.Equal(0, Count<RefundCompleted>(worker, bookingId, e => e.BookingId));
            Assert.Null((await ReadChargeAsync(worker, paymentId)).RefundedAt);
        }

        // Two separate messages, so the inbox does not help here: only the charge itself can say "already refunded".
        [Fact]
        public async Task RefundRequestedAgain_AsANewMessage_ShouldNotRefundASecondTime()
        {
            var ct = TestContext.Current.CancellationToken;
            var worker = _defaultWorker;
            var harness = worker.GetRequiredService<ITestHarness>();
            var bookingId = Interlocked.Increment(ref _nextBookingId);
            var paymentId = await ChargeAsync(worker);
            var request = new RefundRequested(bookingId, 7, 9, DateTime.UtcNow, paymentId, "hold_expired");

            await harness.Bus.Publish(request, ct);
            await DeliveredAsync<RefundCompleted>(worker, bookingId, e => e.BookingId);
            var refundedAt = (await ReadChargeAsync(worker, paymentId)).RefundedAt;

            var secondMessageId = NewId.NextGuid();
            await harness.Bus.Publish(request, context => context.MessageId = secondMessageId, cancellationToken: ct);
            await ConsumedAsync(worker, secondMessageId);

            Assert.Equal(1, Count<RefundCompleted>(worker, bookingId, e => e.BookingId));
            Assert.Equal(0, Count<RefundFailed>(worker, bookingId, e => e.BookingId));
            Assert.Equal(refundedAt, (await ReadChargeAsync(worker, paymentId)).RefundedAt);
        }

        // The same for the payment's result: two separate messages about one charge, one announcement.
        [Fact]
        public async Task PaymentOutcomeAgain_AsANewMessage_ShouldNotBeAnnouncedASecondTime()
        {
            var ct = TestContext.Current.CancellationToken;
            var worker = _defaultWorker;
            var harness = worker.GetRequiredService<ITestHarness>();
            var bookingId = Interlocked.Increment(ref _nextBookingId);
            var paymentId = await ChargeAsync(worker);
            var outcome = new SeatHive.Worker.Messages.PaymentCharged(bookingId, 7, 9, paymentId, true, null);

            await harness.Bus.Publish(outcome, ct);
            await DeliveredAsync<PaymentSucceeded>(worker, bookingId, e => e.BookingId);
            Assert.NotNull((await ReadChargeAsync(worker, paymentId)).AnnouncedAt);

            var secondMessageId = NewId.NextGuid();
            await harness.Bus.Publish(outcome, context => context.MessageId = secondMessageId, cancellationToken: ct);
            await ConsumedAsync(worker, secondMessageId);

            Assert.Equal(1, Count<PaymentSucceeded>(worker, bookingId, e => e.BookingId));
        }

        [Fact]
        public async Task BookingConfirmed_ShouldPublishNotificationSent_MarkedAsSimulated()
        {
            var worker = _defaultWorker;
            var harness = worker.GetRequiredService<ITestHarness>();
            var bookingId = Interlocked.Increment(ref _nextBookingId);

            await harness.Bus.Publish(new BookingConfirmed(bookingId, 7, 9, DateTime.UtcNow, Guid.NewGuid()), TestContext.Current.CancellationToken);

            var sent = await DeliveredAsync<NotificationSent>(worker, bookingId, e => e.BookingId);
            Assert.Equal((7, 9), (sent.SeatId, sent.UserId));
            // Nothing is really sent, and the event says so.
            Assert.Equal(BookingConfirmedConsumer.Channel, sent.Channel);
            Assert.Equal("simulated", sent.Channel);
        }

        // ---- The same message twice at the very same moment ----
        //
        // A broker may deliver a message twice, and the two deliveries may be worked on at the same time.
        // On a Worker that is already warm they really overlap, so this is tried many times on the shared one:
        // before the fix about one try in fifteen ran the consumer twice.

        private const int OverlapTries = 100;

        // Sends createMessage(bookingId, paymentId) twice at once, OverlapTries times, each time for a new booking,
        // and returns for every booking what came out. withCharge first makes the provider charge the payment,
        // so the payment exists on the provider's side like it would in the real flow.
        private async Task<List<List<TResult>>> DeliverEachTwiceAtOnceAsync<TMessage, TResult>(
            Func<int, Guid, TMessage> createMessage, Func<TResult, int> bookingIdOf, bool withCharge)
            where TMessage : class
            where TResult : class
        {
            var worker = _defaultWorker;
            var harness = worker.GetRequiredService<ITestHarness>();
            var log = worker.GetRequiredService<EventLog>();
            var bookingIds = new List<int>();

            for (var i = 0; i < OverlapTries; i++)
            {
                var bookingId = Interlocked.Increment(ref _nextBookingId);
                var paymentId = Guid.NewGuid();
                if (withCharge)
                {
                    await using var scope = worker.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<SimulatedPaymentProvider>().ChargeAsync(paymentId, forceFailure: false);
                }

                var message = createMessage(bookingId, paymentId);
                await Task.WhenAll(
                    Task.Run(() => harness.Bus.Publish(message, context => context.MessageId = paymentId)),
                    Task.Run(() => harness.Bus.Publish(message, context => context.MessageId = paymentId)));
                bookingIds.Add(bookingId);
            }

            foreach (var bookingId in bookingIds)
            {
                await log.WaitForAsync<TResult>(result => bookingIdOf(result) == bookingId);
            }

            // A second result would come right behind the first. Wait until nothing new has arrived for a while.
            var seen = -1;
            while (true)
            {
                var now = log.Of<TResult>(result => bookingIds.Contains(bookingIdOf(result))).Count;
                if (now == seen) break;
                seen = now;
                await Task.Delay(500);
            }

            return bookingIds.Select(bookingId => log.Of<TResult>(result => bookingIdOf(result) == bookingId)).ToList();
        }

        // Each try must have produced its result once, and that result must have been sent once.
        private static void AssertOneResultEach<TResult>(List<List<TResult>> results)
        {
            var producedMoreThanOnce = results.Count(r => r.Distinct().Count() > 1);
            var sentMoreThanOnce = results.Count(r => r.Count > 1);

            Assert.True(producedMoreThanOnce == 0 && sentMoreThanOnce == 0,
                $"Of {results.Count} tries, {producedMoreThanOnce} produced more than one result (the consumer ran twice) " +
                $"and {sentMoreThanOnce} sent a result more than once.");
        }

        [Fact]
        public async Task SamePaymentOutcome_ArrivingTwiceAtOnce_ShouldAnnounceOneResult()
        {
            var results = await DeliverEachTwiceAtOnceAsync<SeatHive.Worker.Messages.PaymentCharged, PaymentSucceeded>(
                (bookingId, paymentId) => new SeatHive.Worker.Messages.PaymentCharged(bookingId, 7, 9, paymentId, true, null),
                result => result.BookingId,
                withCharge: true);

            AssertOneResultEach(results);
        }

        [Fact]
        public async Task SameRefundRequest_ArrivingTwiceAtOnce_ShouldRefundOnce()
        {
            var results = await DeliverEachTwiceAtOnceAsync<RefundRequested, RefundCompleted>(
                (bookingId, paymentId) => new RefundRequested(bookingId, 7, 9, DateTime.UtcNow, paymentId, "hold_expired"),
                result => result.BookingId,
                withCharge: true);

            AssertOneResultEach(results);
        }

        // The notification has nothing of its own to check against: here only the inbox keeps it to one.
        [Fact]
        public async Task SameConfirmation_ArrivingTwiceAtOnce_ShouldNotifyOnce()
        {
            var results = await DeliverEachTwiceAtOnceAsync<BookingConfirmed, NotificationSent>(
                (bookingId, paymentId) => new BookingConfirmed(bookingId, 7, 9, DateTime.UtcNow, paymentId),
                result => result.BookingId,
                withCharge: false);

            AssertOneResultEach(results);
        }
    }
}
