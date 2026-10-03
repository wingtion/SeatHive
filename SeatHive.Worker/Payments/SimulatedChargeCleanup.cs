using Microsoft.Extensions.Options;

namespace SeatHive.Worker.Payments
{
    // Periodically deletes the provider's charges that are older than the retention period,
    // so the table does not grow forever.
    public class SimulatedChargeCleanup : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _interval;
        private readonly ILogger<SimulatedChargeCleanup> _logger;

        public SimulatedChargeCleanup(
            IServiceScopeFactory scopeFactory,
            TimeProvider timeProvider,
            IOptions<PaymentOptions> options,
            ILogger<SimulatedChargeCleanup> logger)
        {
            _scopeFactory = scopeFactory;
            _timeProvider = timeProvider;
            _interval = TimeSpan.FromMinutes(options.Value.CleanupIntervalMinutes);
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(_interval, _timeProvider);

            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    await CleanUpAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // The application is shutting down.
            }
        }

        private async Task CleanUpAsync(CancellationToken stoppingToken)
        {
            try
            {
                // The provider is scoped, so every run gets its own scope.
                using var scope = _scopeFactory.CreateScope();
                var provider = scope.ServiceProvider.GetRequiredService<ISimulatedPaymentProvider>();

                var deleted = await provider.DeleteExpiredChargesAsync(stoppingToken);
                if (deleted > 0)
                {
                    _logger.LogInformation("Deleted {Count} simulated charge(s) older than the retention period.", deleted);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One failed run must not stop the cleanup; the next tick tries again.
                _logger.LogError(ex, "Deleting old simulated charges failed.");
            }
        }
    }
}
