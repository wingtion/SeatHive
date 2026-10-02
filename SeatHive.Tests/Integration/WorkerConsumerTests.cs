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
    [Collection(ContainersCollection.Name)]
    public class WorkerConsumerTests
    {
        private static int _nextBookingId = 1_000_000;

        private readonly ContainersFixture _fixture;

        public WorkerConsumerTests(ContainersFixture fixture)
        {
            _fixture = fixture;
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

            public Task<int> DeleteExpiredChargesAsync(CancellationToken cancellationToken = default)
            {
                return _inner.DeleteExpiredChargesAsync(cancellationToken);
            }
        }

        // connectionString picks another database on the same server; the default is the shared test database.
        private async Task<ServiceProvider> StartWorkerAsync(
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
                o.UseNpgsql(connectionString ?? _fixture.GetPostgresConnectionString(), WorkerDbContext.ConfigureNpgsql));

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
            await using var worker = await StartWorkerAsync(failureRate: 0);
            var harness = worker.GetRequiredService<ITestHarness>();
            var request = NewPaymentRequest();

            await harness.Bus.Publish(request);

            var succeeded = await DeliveredAsync<PaymentSucceeded>(worker, request.BookingId, e => e.BookingId);
            Assert.Equal((request.SeatId, request.UserId, request.PaymentId), (succeeded.SeatId, succeeded.UserId, succeeded.PaymentId));
            Assert.Equal(0, Count<PaymentFailed>(worker, request.BookingId, e => e.BookingId));
        }

        [Fact]
        public async Task PaymentRequested_ShouldPublishPaymentFailed_WhenFailureIsForced()
        {
            // Random failures are off, so only the flag on the request can make it fail.
            await using var worker = await StartWorkerAsync(failureRate: 0);
            var harness = worker.GetRequiredService<ITestHarness>();
            var request = NewPaymentRequest(forceFailure: true);

            await harness.Bus.Publish(request);

            var failed = await DeliveredAsync<PaymentFailed>(worker, request.BookingId, e => e.BookingId);
            Assert.Equal((request.SeatId, request.UserId, request.PaymentId), (failed.SeatId, failed.UserId, failed.PaymentId));
            Assert.False(string.IsNullOrWhiteSpace(failed.Reason));
            Assert.Equal(0, Count<PaymentSucceeded>(worker, request.BookingId, e => e.BookingId));
        }

        [Fact]
        public async Task PaymentRequested_ShouldPublishPaymentFailed_WhenTheFailureRateIsOne()
        {
            await using var worker = await StartWorkerAsync(failureRate: 1);
            var harness = worker.GetRequiredService<ITestHarness>();
            var request = NewPaymentRequest();

            await harness.Bus.Publish(request);

            await DeliveredAsync<PaymentFailed>(worker, request.BookingId, e => e.BookingId);
            Assert.Equal(0, Count<PaymentSucceeded>(worker, request.BookingId, e => e.BookingId));
        }

        [Fact]
        public async Task ProviderCall_ShouldRunWithoutAnOpenDatabaseTransaction()
        {
            // A database of its own, so every other session in it belongs to this Worker.
            var connectionString = _fixture.GetPostgresConnectionString("seathive_worker_tx");
            var clock = new GatedClock();
            await using var worker = await StartWorkerAsync(failureRate: 0, clock, delayMs: 1000, connectionString);
            var harness = worker.GetRequiredService<ITestHarness>();
            var request = NewPaymentRequest();

            var waiting = clock.NextWaitAsync();
            await harness.Bus.Publish(request);

            // The payment is now "at the provider": the consumer waits on the clock, which does not move.
            await waiting.WaitAsync(TimeSpan.FromSeconds(10));
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
            await using var worker = await StartWorkerAsync(failureRate: 0);
            var harness = worker.GetRequiredService<ITestHarness>();
            var calls = worker.GetRequiredService<ChargeCalls>();
            var request = NewPaymentRequest();
            var messageId = NewId.NextGuid();

            // The same request arrives twice: redelivered by the broker (same message id)
            // or sent again by the publisher (a new message id).
            await harness.Bus.Publish(request, context => context.MessageId = messageId);
            await harness.Bus.Publish(request, context => context.MessageId = sameMessageId ? messageId : NewId.NextGuid());

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
            await using var worker = await StartWorkerAsync(failureRate: 0);
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
            await using var worker = await StartWorkerAsync(failureRate: 0);

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
            await using var worker = await StartWorkerAsync(failureRate: 0);
            var harness = worker.GetRequiredService<ITestHarness>();
            var bookingId = Interlocked.Increment(ref _nextBookingId);
            var paymentId = Guid.NewGuid();

            await harness.Bus.Publish(new RefundRequested(bookingId, 7, 9, DateTime.UtcNow, paymentId, "hold_expired"));

            var refunded = await DeliveredAsync<RefundCompleted>(worker, bookingId, e => e.BookingId);
            Assert.Equal((7, 9, paymentId), (refunded.SeatId, refunded.UserId, refunded.PaymentId));
        }

        [Fact]
        public async Task BookingConfirmed_ShouldPublishNotificationSent_MarkedAsSimulated()
        {
            await using var worker = await StartWorkerAsync(failureRate: 0);
            var harness = worker.GetRequiredService<ITestHarness>();
            var bookingId = Interlocked.Increment(ref _nextBookingId);

            await harness.Bus.Publish(new BookingConfirmed(bookingId, 7, 9, DateTime.UtcNow, Guid.NewGuid()));

            var sent = await DeliveredAsync<NotificationSent>(worker, bookingId, e => e.BookingId);
            Assert.Equal((7, 9), (sent.SeatId, sent.UserId));
            // Nothing is really sent, and the event says so.
            Assert.Equal(BookingConfirmedConsumer.Channel, sent.Channel);
            Assert.Equal("simulated", sent.Channel);
        }
    }
}
