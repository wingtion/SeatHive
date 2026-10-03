using System.Collections.Concurrent;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using SeatHive.Shared.Events;

namespace SeatHive.Tests.Integration.Api
{
    // Every event that reached a subscriber in a test host, in the order it arrived.
    public class EventLog
    {
        private readonly ConcurrentQueue<object> _events = new();

        public void Add(object message) => _events.Enqueue(message);

        public List<T> Of<T>(Func<T, bool> match) => _events.OfType<T>().Where(match).ToList();

        // Delivery happens in the background, so it is polled for. Fails after 10 seconds.
        public async Task<T> WaitForAsync<T>(Func<T, bool> match) where T : class
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                var delivered = Of(match).FirstOrDefault();
                if (delivered != null) return delivered;

                Assert.True(DateTime.UtcNow < deadline, $"No matching {typeof(T).Name} was delivered.");
                await Task.Delay(20);
            }
        }
    }

    // The outbox delivers events to the broker without the bus "publishing" them, so the test harness
    // does not list them as published. A subscriber does see them: these consumers only write down
    // what they receive, like a real subscriber would see it.
    public class EventRecorder<T> : IConsumer<T> where T : class
    {
        private readonly EventLog _log;

        public EventRecorder(EventLog log)
        {
            _log = log;
        }

        public Task Consume(ConsumeContext<T> context)
        {
            _log.Add(context.Message);
            return Task.CompletedTask;
        }
    }

    public static class EventRecorderRegistration
    {
        // One subscriber per event type.
        public static void AddEventRecorders(this IBusRegistrationConfigurator configurator)
        {
            configurator.AddSingleton<EventLog>();

            configurator.AddRecorder<SeatHeld>();
            configurator.AddRecorder<HoldReleased>();
            configurator.AddRecorder<HoldExpired>();
            configurator.AddRecorder<PaymentRequested>();
            configurator.AddRecorder<PaymentSucceeded>();
            configurator.AddRecorder<PaymentFailed>();
            configurator.AddRecorder<BookingConfirmed>();
            configurator.AddRecorder<RefundRequested>();
            configurator.AddRecorder<RefundCompleted>();
            configurator.AddRecorder<RefundFailed>();
            configurator.AddRecorder<NotificationSent>();
            configurator.AddRecorder<DemoDataReset>();
            configurator.AddRecorder<RaceFinished>();
        }

        // On an endpoint of its own. By default the recorder for PaymentSucceeded would get the same endpoint name
        // as PaymentSucceededConsumer and share that consumer's endpoint, inbox included.
        private static void AddRecorder<T>(this IBusRegistrationConfigurator configurator) where T : class
        {
            configurator.AddConsumer<EventRecorder<T>>().Endpoint(e => e.Name = $"recorder-{typeof(T).Name}");
        }
    }
}
