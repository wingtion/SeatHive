namespace SeatHive.Api.Services
{
    public static class CorsSetup
    {
        public const string SettingName = "Cors:AllowedOrigins";

        // A browser page on another origin (the front end) may only call the API, and connect to the hub,
        // from an origin named here. Each entry is exactly an origin such as https://seathive.example:
        // no "*", no path, not even a trailing "/". Credentials are allowed, which is why "*" never is.
        // Returns whether any origin is allowed, which is whether the CORS middleware is needed at all.
        public static bool AddAllowedOrigins(this IServiceCollection services, IConfiguration configuration)
        {
            // Empty entries are skipped, so an unset variable in docker compose means "no origin".
            var origins = (configuration.GetSection(SettingName).Get<string[]>() ?? [])
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .Select(entry => entry.Trim())
                .ToList();
            if (origins.Count == 0) return false;

            foreach (var origin in origins)
            {
                if (!IsOrigin(origin))
                {
                    throw new InvalidOperationException(
                        $"Setting '{SettingName}' has the invalid entry '{origin}'. " +
                        "Use an origin such as https://seathive.example: http or https, no wildcard, no path and no trailing '/'.");
                }
            }

            services.AddCors(options => options.AddDefaultPolicy(policy => policy
                .WithOrigins(origins.ToArray())
                .AllowCredentials()
                .WithHeaders("Authorization", "Content-Type", "X-Requested-With", "X-SignalR-User-Agent")
                .WithMethods("GET", "POST")));

            // CORS does not apply to the WebSocket handshake. The WebSocket middleware (which the hub uses)
            // turns away a handshake from an origin that is not in this list.
            services.Configure<WebSocketOptions>(options =>
            {
                foreach (var origin in origins) options.AllowedOrigins.Add(origin);
            });

            return true;
        }

        private static bool IsOrigin(string entry)
        {
            return !entry.Contains('*')
                && Uri.TryCreate(entry, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && string.IsNullOrEmpty(uri.UserInfo)
                // Scheme, host and port only: anything after them (a path, "/", a query) makes it differ.
                && string.Equals(uri.GetLeftPart(UriPartial.Authority), entry, StringComparison.OrdinalIgnoreCase);
        }
    }
}
