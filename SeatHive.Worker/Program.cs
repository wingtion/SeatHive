using MassTransit;
using Microsoft.EntityFrameworkCore;
using SeatHive.Worker;
using SeatHive.Worker.Data;
using SeatHive.Worker.Payments;

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

var dbConnectionString = RequiredSetting("ConnectionStrings:DefaultConnection");
var rabbitUsername = RequiredSetting("RabbitMQ:Username");
var rabbitPassword = RequiredSetting("RabbitMQ:Password");

builder.Services.AddDbContext<WorkerDbContext>(options =>
    options.UseNpgsql(dbConnectionString, WorkerDbContext.ConfigureNpgsql));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<PaymentOptions>(builder.Configuration.GetSection(PaymentOptions.SectionName));
builder.Services.AddScoped<ISimulatedPaymentProvider, SimulatedPaymentProvider>();
builder.Services.AddHostedService<SimulatedChargeCleanup>();

builder.Services.AddMassTransit(x =>
{
    x.AddWorkerConsumers();
    x.AddWorkerOutbox();

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

// Apply the Worker's migrations at startup. Retry briefly in case the database is not accepting connections yet.
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WorkerDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    const int maxAttempts = 10;

    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await db.Database.MigrateAsync();
            break;
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            logger.LogWarning(ex, "Database migration attempt {Attempt}/{MaxAttempts} failed; retrying in 3 seconds.", attempt, maxAttempts);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }
}

host.Run();
