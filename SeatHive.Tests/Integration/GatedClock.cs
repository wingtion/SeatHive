using Microsoft.Extensions.Time.Testing;

namespace SeatHive.Tests.Integration
{
    // A clock that stands still until a test moves it, and that can tell when something starts waiting on it.
    public sealed class GatedClock : FakeTimeProvider
    {
        private readonly object _gate = new();
        private TaskCompletionSource _nextWait = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GatedClock()
        {
        }

        public GatedClock(DateTimeOffset start) : base(start)
        {
        }

        // Completes when something starts to wait on this clock (a delay, a periodic timer) after this call.
        // Ask for it before triggering the code that will wait.
        public Task NextWaitAsync()
        {
            lock (_gate)
            {
                _nextWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _nextWait.Task;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);

            lock (_gate)
            {
                _nextWait.TrySetResult();
            }

            return timer;
        }
    }
}
