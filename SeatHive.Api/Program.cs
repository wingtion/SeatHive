using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text.Json;
using System.Text.Json.Serialization;
using SeatHive.Api.Consumers;
using SeatHive.Api.Data;
using SeatHive.Api.Hubs;
using SeatHive.Api.Services;
using StackExchange.Redis;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

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

var jwtKey = RequiredSetting("Jwt:Key");
if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
{
    throw new InvalidOperationException("Required setting 'Jwt:Key' must be at least 32 bytes long.");
}

var dbConnectionString = RequiredSetting("ConnectionStrings:DefaultConnection");
var redisConnectionString = RequiredSetting("ConnectionStrings:Redis");
var rabbitUsername = RequiredSetting("RabbitMQ:Username");
var rabbitPassword = RequiredSetting("RabbitMQ:Password");

// Add services to the container.
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(dbConnectionString));

// Behind the reverse proxy the client's address and scheme come from the forwarded headers of the trusted proxies.
var behindTrustedProxy = builder.Services.AddTrustedProxies(builder.Configuration);

// Errors that no controller writes (no token, wrong role, rate limit) get the same ProblemDetails body with a "code".
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        var code = ErrorCodes.ForStatus(context.ProblemDetails.Status);
        if (code != null) context.ProblemDetails.Extensions.TryAdd("code", code);
    };
});

builder.Services.AddControllers()
    // Enums are sent as camelCase strings everywhere ("held", "paymentPending"), like the property names.
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)))
    .ConfigureApiBehaviorOptions(options =>
{
    // Validation errors keep the standard ProblemDetails shape and get a machine-readable code like every other error.
    options.InvalidModelStateResponseFactory = context =>
    {
        var problem = new ValidationProblemDetails(context.ModelState) { Status = StatusCodes.Status400BadRequest };
        problem.Extensions["code"] = ErrorCodes.ValidationFailed;

        return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
    };
});

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<PaymentSucceededConsumer, InboxConsumerDefinition<PaymentSucceededConsumer>>();
    x.AddConsumer<PaymentFailedConsumer, InboxConsumerDefinition<PaymentFailedConsumer>>();
    x.AddConsumer<BookingHistoryConsumer, BookingHistoryConsumerDefinition>();
    // Live updates: what the hub sends starts here, when an event arrives from the bus, never in the request.
    x.AddConsumer<SeatStatusBroadcaster, SeatStatusBroadcasterDefinition>();
    x.AddConsumer<BookingLiveNotifier, BookingLiveNotifierDefinition>();

    x.AddBookingOutbox(builder.Configuration);

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

var redisOptions = ConfigurationOptions.Parse(redisConnectionString);
redisOptions.AbortOnConnectFail = false;

var redisConnection = ConnectionMultiplexer.Connect(redisOptions);
builder.Services.AddSingleton<IConnectionMultiplexer>(redisConnection);

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo { Title = "SeatHive API", Version = "v1" });

    // Define the Security Scheme. With the "http" type Swagger UI adds "Bearer " itself: paste the token only.
    c.AddSecurityDefinition(AuthorizeOperationFilter.SchemeName, new Microsoft.OpenApi.OpenApiSecurityScheme
    {
        Description = "JWT from POST /api/auth/login. Paste the token only.",
        Type = Microsoft.OpenApi.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });

    // Apply the Security Scheme to the endpoints that ask for a token, not to the anonymous ones.
    c.OperationFilter<AuthorizeOperationFilter>();
});
builder.Services.AddScoped<SeatHive.Api.Services.BookingService>();
builder.Services.AddScoped<SeatHive.Api.Services.IRedisLockService, SeatHive.Api.Services.RedisLockService>();
builder.Services.AddScoped<SeatHive.Api.Services.AuthService>();
builder.Services.AddScoped<SeatStatusQuery>();
builder.Services.AddScoped<BookingHistory>();

builder.Services.AddSignalR()
    // The same JSON as the HTTP API: camelCase names, enums as camelCase strings.
    .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
// "The user" of a hub connection is the user id in the token.
builder.Services.AddSingleton<Microsoft.AspNetCore.SignalR.IUserIdProvider, SubUserIdProvider>();

// Hold timing goes through TimeProvider so tests can move the clock.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<HoldOptions>(builder.Configuration.GetSection(HoldOptions.SectionName));
builder.Services.AddHostedService<HoldExpirySweeper>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Keep the claim names exactly as they are in the token ("sub", "role").
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            NameClaimType = "sub",
            RoleClaimType = "role"
        };

        // A browser cannot set the Authorization header on a WebSocket, so the SignalR client sends the token
        // as the "access_token" query parameter. It is read from there for the hub only: everywhere else
        // a token in the address is ignored, as before.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments(SeatHub.Path))
                {
                    context.Token = token;
                }

                return Task.CompletedTask;
            }
        };
    });

// Requests per minute. Auth is limited per IP address, booking per user, reads per user or (without a token) per IP address.
var authPermitLimit = builder.Configuration.GetValue("RateLimiting:Auth:PermitLimit", 10);
var bookingPermitLimit = builder.Configuration.GetValue("RateLimiting:Booking:PermitLimit", 30);
var readPermitLimit = builder.Configuration.GetValue("RateLimiting:Read:PermitLimit", 120);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(RateLimitPolicies.Auth, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = authPermitLimit, Window = TimeSpan.FromMinutes(1) }));

    options.AddPolicy(RateLimitPolicies.Booking, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.User.FindFirst("sub")?.Value ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = bookingPermitLimit, Window = TimeSpan.FromMinutes(1) }));

    // A limit of its own, so reading (and polling) never uses up what a user needs for booking.
    options.AddPolicy(RateLimitPolicies.Read, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.User.FindFirst("sub")?.Value ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = readPermitLimit, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

// Apply migrations at startup. Retry briefly in case the database is not accepting connections yet.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
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
            app.Logger.LogWarning(ex, "Database migration attempt {Attempt}/{MaxAttempts} failed; retrying in 3 seconds.", attempt, maxAttempts);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    await AdminSeeder.SeedAsync(db, app.Configuration, app.Logger);
}

// First, so everything after it (HTTPS redirection, rate limits) sees the real client address and scheme.
if (behindTrustedProxy)
{
    app.UseForwardedHeaders();
}

// Gives a response that has a status code but no body (401, 403, 429) a ProblemDetails body.
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

// After authentication, so the booking limit can be counted per user.
app.UseRateLimiter();

app.MapControllers();

// A connection is closed when its token runs out, instead of living on for as long as it stays open.
app.MapHub<SeatHub>(SeatHub.Path, options => options.CloseOnAuthenticationExpiration = true);

app.Run();
