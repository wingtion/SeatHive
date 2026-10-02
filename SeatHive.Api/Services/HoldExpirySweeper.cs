using Microsoft.Extensions.Options;

namespace SeatHive.Api.Services
{
    // Periodically expires holds that ran out, so their seats become free
    // even when nobody tries to hold them again.
    public class HoldExpirySweeper : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _interval;
        private readonly ILogger<HoldExpirySweeper> _logger;

        public HoldExpirySweeper(
            IServiceScopeFactory scopeFactory,
            TimeProvider timeProvider,
            IOptions<HoldOptions> options,
            ILogger<HoldExpirySweeper> logger)
        {
            _scopeFactory = scopeFactory;
            _timeProvider = timeProvider;
            _interval = TimeSpan.FromSeconds(options.Value.SweepIntervalSeconds);
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(_interval, _timeProvider);

            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    await SweepAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // The application is shutting down.
            }
        }

        private async Task SweepAsync(CancellationToken stoppingToken)
        {
            try
            {
                // BookingService is scoped, so every sweep gets its own scope.
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<BookingService>();

                var expired = await service.ExpireDueHoldsAsync(stoppingToken);
                if (expired > 0)
                {
                    _logger.LogInformation("Expired {Count} hold(s).", expired);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One failed sweep must not stop the sweeper; the next tick tries again.
                _logger.LogError(ex, "Expiring holds failed.");
            }
        }
    }
}
