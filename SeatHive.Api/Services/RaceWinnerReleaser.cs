using System.Collections.Concurrent;
using System.Threading.Channels;

namespace SeatHive.Api.Services
{
    // Gives up the seat a race winner holds once its time is up, through the real release (which publishes
    // HoldReleased). The schedule lives in memory: if the API restarts first, or the release fails, nothing is lost
    // but time, because the hold still runs out by itself after its normal duration and the sweeper expires it.
    public class RaceWinnerReleaser : BackgroundService
    {
        private readonly Channel<PendingRelease> _queue = Channel.CreateUnbounded<PendingRelease>();
        private readonly ConcurrentDictionary<int, DateTime> _pending = new();
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<RaceWinnerReleaser> _logger;

        public RaceWinnerReleaser(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, ILogger<RaceWinnerReleaser> logger)
        {
            _scopeFactory = scopeFactory;
            _timeProvider = timeProvider;
            _logger = logger;
        }

        private record PendingRelease(int BookingId, int UserId, DateTime ReleasesAt);

        public void Schedule(int bookingId, int userId, DateTime releasesAt)
        {
            _pending[bookingId] = releasesAt;
            _queue.Writer.TryWrite(new PendingRelease(bookingId, userId, releasesAt));
        }

        // When a booking scheduled here is released. Null when this process has no such schedule.
        public DateTime? ReleaseTimeOf(int bookingId) => _pending.TryGetValue(bookingId, out var at) ? at : null;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await foreach (var release in _queue.Reader.ReadAllAsync(stoppingToken))
                {
                    _ = ReleaseLaterAsync(release, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // The application is shutting down; the holds run out by themselves.
            }
        }

        private async Task ReleaseLaterAsync(PendingRelease release, CancellationToken stoppingToken)
        {
            try
            {
                // Waits for a point in time, not for a duration, so a late start does not move the release.
                var delay = release.ReleasesAt - _timeProvider.GetUtcNow().UtcDateTime;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, _timeProvider, stoppingToken);

                using var scope = _scopeFactory.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<BookingService>()
                    .ReleaseAsync(release.BookingId, release.UserId);

                if (!result.IsSuccess)
                {
                    // Already gone, for example expired or deleted by a reset of the demo data.
                    _logger.LogInformation("Race winner booking {BookingId} was not released: {Error}.", release.BookingId, result.Error);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Race winner booking {BookingId} could not be released; it runs out by itself.", release.BookingId);
            }
            finally
            {
                _pending.TryRemove(release.BookingId, out _);
            }
        }
    }
}
