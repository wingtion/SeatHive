using MassTransit;
using SeatHive.Api.Data;

namespace SeatHive.Api.Services
{
    public static class OutboxSetup
    {
        // A method of its own because the MassTransit test harness, which rebuilds the bus on an in-memory
        // transport, drops the outbox registration: the tests apply this again.
        public static void AddBookingOutbox(this IBusRegistrationConfigurator configurator, IConfiguration configuration)
        {
            // Transactional outbox: published events are stored with the booking change in one transaction
            // and delivered to RabbitMQ afterwards, so an event is never lost or sent for a change that was rolled back.
            configurator.AddEntityFrameworkOutbox<AppDbContext>(o =>
            {
                o.UsePostgres();
                o.UseBusOutbox();
                // Booking changes are conditional updates; under read committed a concurrent one just matches no row.
                o.IsolationLevel = System.Data.IsolationLevel.ReadCommitted;
                o.QueryDelay = TimeSpan.FromMilliseconds(configuration.GetValue("Outbox:QueryDelayMs", 1000));
            });
        }
    }
}
