using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using SeatHive.Worker;
using SeatHive.Worker.Data;
using SeatHive.Worker.Payments;

namespace SeatHive.Tests.Integration
{
    // The simulated provider forgets charges after the retention period. Time only moves when a test moves it.
    [Collection(ContainersCollection.Name)]
    public class SimulatedChargeRetentionTests
    {
        private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

        private readonly ContainersFixture _fixture;
        private readonly GatedClock _clock = new(Now);

        public SimulatedChargeRetentionTests(ContainersFixture fixture)
        {
            _fixture = fixture;
        }

        // The Worker's provider and its cleanup service on a database of their own,
        // so the tests see exactly the charges they created.
        private async Task<ServiceProvider> BuildWorkerServicesAsync(int? retentionDays = null)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(_clock);
            services.Configure<PaymentOptions>(o =>
            {
                // Payments take no time; the clock in these tests only moves when a test moves it.
                o.MinDelayMs = 0;
                o.MaxDelayMs = 0;
                if (retentionDays != null) o.ChargeRetentionDays = retentionDays.Value;
            });
            services.AddDbContext<WorkerDbContext>(o => o.UseNpgsql(
                _fixture.GetPostgresConnectionString("seathive_worker_retention"), WorkerDbContext.ConfigureNpgsql));
            services.AddScoped<ISimulatedPaymentProvider, SimulatedPaymentProvider>();
            services.AddSingleton<SimulatedChargeCleanup>();

            var provider = services.BuildServiceProvider();

            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkerDbContext>();
            await db.Database.MigrateAsync();
            await db.SimulatedCharges.ExecuteDeleteAsync();

            return provider;
        }

        private static async Task<Guid> AddChargeAsync(ServiceProvider services, DateTimeOffset chargedAt)
        {
            var key = Guid.NewGuid();

            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkerDbContext>();
            db.SimulatedCharges.Add(new SimulatedCharge { IdempotencyKey = key, Succeeded = true, ChargedAt = chargedAt.UtcDateTime });
            await db.SaveChangesAsync();

            return key;
        }

        private static async Task<List<Guid>> RemainingKeysAsync(ServiceProvider services)
        {
            await using var scope = services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<WorkerDbContext>()
                .SimulatedCharges.AsNoTracking().Select(c => c.IdempotencyKey).ToListAsync();
        }

        private static async Task<int> DeleteExpiredAsync(ServiceProvider services)
        {
            await using var scope = services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ISimulatedPaymentProvider>().DeleteExpiredChargesAsync();
        }

        [Fact]
        public async Task DeleteExpiredCharges_ShouldRemoveOnlyChargesOlderThanSevenDays_ByDefault()
        {
            await using var services = await BuildWorkerServicesAsync();
            var eightDaysOld = await AddChargeAsync(services, Now.AddDays(-8));
            var justOverSevenDaysOld = await AddChargeAsync(services, Now.AddDays(-7).AddSeconds(-1));
            var exactlySevenDaysOld = await AddChargeAsync(services, Now.AddDays(-7));
            var sixDaysOld = await AddChargeAsync(services, Now.AddDays(-6));
            var fresh = await AddChargeAsync(services, Now);

            var deleted = await DeleteExpiredAsync(services);

            Assert.Equal(2, deleted);
            var remaining = await RemainingKeysAsync(services);
            Assert.DoesNotContain(eightDaysOld, remaining);
            Assert.DoesNotContain(justOverSevenDaysOld, remaining);
            Assert.Equal(new[] { exactlySevenDaysOld, sixDaysOld, fresh }.Order(), remaining.Order());

            // Nothing left to delete.
            Assert.Equal(0, await DeleteExpiredAsync(services));
        }

        [Fact]
        public async Task DeleteExpiredCharges_ShouldUseTheConfiguredRetention()
        {
            await using var services = await BuildWorkerServicesAsync(retentionDays: 1);
            var twoDaysOld = await AddChargeAsync(services, Now.AddDays(-2));
            var halfADayOld = await AddChargeAsync(services, Now.AddHours(-12));

            var deleted = await DeleteExpiredAsync(services);

            Assert.Equal(1, deleted);
            Assert.Equal(new[] { halfADayOld }, await RemainingKeysAsync(services));
            Assert.DoesNotContain(twoDaysOld, await RemainingKeysAsync(services));
        }

        [Fact]
        public async Task AChargeThatWasForgotten_ShouldBeChargedAgain_AndOneThatIsKeptShouldNot()
        {
            await using var services = await BuildWorkerServicesAsync();
            var forgotten = await AddChargeAsync(services, Now.AddDays(-8));
            var kept = await AddChargeAsync(services, Now.AddDays(-6));
            await DeleteExpiredAsync(services);

            async Task<ChargeResult> ChargeAsync(Guid key)
            {
                await using var scope = services.CreateAsyncScope();
                // Both stored charges had succeeded. A new charge fails, because failure is forced.
                return await scope.ServiceProvider.GetRequiredService<ISimulatedPaymentProvider>().ChargeAsync(key, forceFailure: true);
            }

            // This is why the retention must outlast any redelivery of a payment request.
            Assert.False((await ChargeAsync(forgotten)).Succeeded);
            Assert.True((await ChargeAsync(kept)).Succeeded);
        }

        [Fact]
        public async Task CleanupService_ShouldDeleteOldCharges_EveryInterval()
        {
            await using var services = await BuildWorkerServicesAsync();
            var cleanup = services.GetRequiredService<SimulatedChargeCleanup>();
            var old = await AddChargeAsync(services, Now.AddDays(-8));
            var recent = await AddChargeAsync(services, Now.AddDays(-1));

            // The service starts its loop in the background; wait until it is really waiting for its first interval.
            var waiting = _clock.NextWaitAsync();
            await cleanup.StartAsync(CancellationToken.None);
            await waiting.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                // Nothing happens before the first interval has passed (60 minutes by default).
                _clock.Advance(TimeSpan.FromMinutes(59));
                Assert.Contains(old, await RemainingKeysAsync(services));

                _clock.Advance(TimeSpan.FromMinutes(1));

                // The run happens in the background, so its effect is polled for.
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while ((await RemainingKeysAsync(services)).Contains(old))
                {
                    Assert.True(DateTime.UtcNow < deadline, "The old charge was not deleted.");
                    await Task.Delay(20);
                }

                Assert.Equal(new[] { recent }, await RemainingKeysAsync(services));
            }
            finally
            {
                await cleanup.StopAsync(CancellationToken.None);
            }
        }
    }
}
