using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace SeatHive.Api.Services
{
    public static class ReverseProxySetup
    {
        public const string SettingName = "ReverseProxy:TrustedProxies";

        // Behind a reverse proxy every request arrives from the proxy, so the client's address (which the limits
        // per IP address need) and the original scheme come from X-Forwarded-For and X-Forwarded-Proto.
        // They are only believed when the request really came from one of the configured proxies:
        // anyone can send these headers. With nothing configured they are ignored.
        // Returns whether any proxy is trusted, which is whether the middleware is needed at all.
        public static bool AddTrustedProxies(this IServiceCollection services, IConfiguration configuration)
        {
            // An entry is an address ("172.28.0.5") or a network ("172.28.0.0/24"). Empty entries are skipped,
            // so an unset variable in docker compose means "no proxy".
            var entries = (configuration.GetSection(SettingName).Get<string[]>() ?? [])
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .Select(entry => entry.Trim())
                .ToList();
            if (entries.Count == 0) return false;

            var proxies = new List<IPAddress>();
            var networks = new List<System.Net.IPNetwork>();

            foreach (var entry in entries)
            {
                if (entry.Contains('/') && System.Net.IPNetwork.TryParse(entry, out var network))
                {
                    networks.Add(network);
                }
                else if (!entry.Contains('/') && IPAddress.TryParse(entry, out var address))
                {
                    proxies.Add(address);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Setting '{SettingName}' has the invalid entry '{entry}'. Use an IP address or a network such as 172.28.0.0/24.");
                }
            }

            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                // One proxy in front of the API: only the address it added itself counts,
                // not what a client wrote into the header before it.
                options.ForwardLimit = 1;

                // Only what is configured is trusted, not the loopback defaults.
                options.KnownProxies.Clear();
                options.KnownIPNetworks.Clear();
                foreach (var proxy in proxies) options.KnownProxies.Add(proxy);
                foreach (var network in networks) options.KnownIPNetworks.Add(network);
            });

            return true;
        }
    }
}
