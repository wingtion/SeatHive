using System.Globalization;
using Microsoft.Extensions.Options;

namespace SeatHive.Api.Services
{
    // Resets the demo data once a day at DemoReset:DailyAtUtc, so a deployment that is always on starts every day
    // with free seats. Does nothing when that setting is not set.
    public class NightlyDemoReset : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeProvider _timeProvider;
        private readonly TimeOnly? _dailyAt;
        private readonly ILogger<NightlyDemoReset> _logger;

        public NightlyDemoReset(
            IServiceScopeFactory scopeFactory,
            TimeProvider timeProvider,
            IOptions<DemoResetOptions> options,
            ILogger<NightlyDemoReset> logger)
        {
            _scopeFactory = scopeFactory;
            _timeProvider = timeProvider;
            _dailyAt = ParseDailyAt(options.Value.DailyAtUtc);
            _logger = logger;
        }

        // A value that is not a time of day stops the application at startup, like a missing required setting.
        public static TimeOnly? ParseDailyAt(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            if (!TimeOnly.TryParseExact(value.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                throw new InvalidOperationException(
                    $"Setting '{DemoResetOptions.SectionName}:{nameof(DemoResetOptions.DailyAtUtc)}' must be a time of day " +
                    $"as HH:mm (UTC), for example 03:00; got '{value}'.");
            }

            return time;
        }

        // How long from now (UTC) until the clock next shows dailyAt. At exactly that time: a whole day.
        public static TimeSpan TimeUntilNext(DateTimeOffset now, TimeOnly dailyAt)
        {
            var utcNow = now.UtcDateTime;
            var next = utcNow.Date + dailyAt.ToTimeSpan();
            if (next <= utcNow) next = next.AddDays(1);

            return next - utcNow;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_dailyAt is not { } dailyAt) return;

            _logger.LogInformation("The demo data is reset every day at {DailyAt} UTC.", dailyAt.ToString("HH:mm", CultureInfo.InvariantCulture));

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeUntilNext(_timeProvider.GetUtcNow(), dailyAt), _timeProvider, stoppingToken);
                    await ResetAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // The application is shutting down.
            }
        }

        private async Task ResetAsync(CancellationToken stoppingToken)
        {
            try
            {
                // DemoDataResetter is scoped, so every reset gets its own scope.
                using var scope = _scopeFactory.CreateScope();
                var resetter = scope.ServiceProvider.GetRequiredService<DemoDataResetter>();

                var result = await resetter.ResetAsync(stoppingToken);
                _logger.LogInformation(
                    "Nightly reset of the demo data: {Seats} seats created, {Guests} guest(s) removed.",
                    result.SeatsCreated, result.GuestsRemoved);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One failed reset must not stop the schedule; the next night tries again.
                _logger.LogError(ex, "The nightly reset of the demo data failed.");
            }
        }
    }
}
