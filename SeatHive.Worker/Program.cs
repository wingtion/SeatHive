using MassTransit;
using SeatHive.Worker.Consumers;

var builder = Host.CreateApplicationBuilder(args);

// Secrets are never in the repository. They come from user-secrets (local development)
// or environment variables (.env for docker compose). A missing one stops the application here.
string RequiredSetting(string key)
{
    var value = builder.Configuration[key];
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException(
            $"Required setting '{key}' is not configured. " +
            "Set it with 'dotnet user-secrets' for local development or in .env for docker compose (see .env.example).");
    }
    return value;
}

var rabbitUsername = RequiredSetting("RabbitMQ:Username");
var rabbitPassword = RequiredSetting("RabbitMQ:Password");

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<BookingConsumer>();

    // 2. Connect to RabbitMQ
    x.UsingRabbitMq((context, cfg) =>
    {
        var rabbitHost = builder.Configuration["RabbitMQ:HostName"] ?? "localhost";

        cfg.Host(rabbitHost, "/", h =>
        {
            h.Username(rabbitUsername);
            h.Password(rabbitPassword);
        });

        cfg.ConfigureEndpoints(context);
    });
});

var host = builder.Build();
host.Run();
