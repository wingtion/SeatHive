using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SeatHive.Tests.Integration
{
    // What a server exposes. The compose files are read the way Docker Compose itself reads them
    // ("docker compose config"), with the override merged in for the local case, so the answer is what would run.
    // On a server only Caddy publishes ports, Caddy forwards to the API only, and the API trusts Caddy's address only:
    // Postgres, Redis and RabbitMQ (its management UI included) are reachable from inside the compose network only.
    [Trait(TestCategories.Trait, TestCategories.Integration)]
    public class ServerExposureTests
    {
        private const string ServerFile = "docker-compose.yml";
        private const string LocalFile = "docker-compose.override.yml";

        private sealed record Published(string Service, string HostIp, string Port, string Protocol);

        private static async Task<JsonElement> ComposeConfigAsync(bool local)
        {
            var start = new ProcessStartInfo("docker")
            {
                WorkingDirectory = ComposeImages.RepositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in new[] { "compose", "-f", ServerFile }) start.ArgumentList.Add(argument);
            // Locally Caddy only runs with its profile; it is asked for here so it is checked too.
            if (local) foreach (var argument in new[] { "-f", LocalFile, "--profile", "proxy" }) start.ArgumentList.Add(argument);
            foreach (var argument in new[] { "config", "--format", "json" }) start.ArgumentList.Add(argument);

            // The required secrets only have to be set for the files to be read; they do not change what is published.
            foreach (var key in new[] { "POSTGRES_PASSWORD", "REDIS_PASSWORD", "RABBITMQ_USER", "RABBITMQ_PASSWORD", "JWT_KEY" })
            {
                start.Environment[key] = "not-a-real-secret";
            }

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, $"docker compose config failed: {await error}");

            return JsonDocument.Parse(await output).RootElement.Clone();
        }

        private static List<Published> PublishedPorts(JsonElement config)
        {
            var published = new List<Published>();
            foreach (var service in config.GetProperty("services").EnumerateObject())
            {
                if (!service.Value.TryGetProperty("ports", out var ports)) continue;
                foreach (var port in ports.EnumerateArray())
                {
                    published.Add(new Published(
                        service.Name,
                        port.TryGetProperty("host_ip", out var ip) ? ip.GetString() ?? "" : "",
                        port.GetProperty("published").GetString()!,
                        port.GetProperty("protocol").GetString()!));
                }
            }
            return published;
        }

        [Fact]
        public async Task Server_ShouldPublishTheProxyOnly()
        {
            var published = PublishedPorts(await ComposeConfigAsync(local: false));

            Assert.All(published, p => Assert.Equal("caddy", p.Service));
            Assert.Equal(
                new[] { "443/tcp", "443/udp", "80/tcp" },
                published.Select(p => $"{p.Port}/{p.Protocol}").Order());

            // Named, so a failure says what leaked: the API, Postgres, Redis, RabbitMQ and its management UI.
            foreach (var port in new[] { "8080", "5432", "6379", "5672", "15672" })
            {
                Assert.DoesNotContain(published, p => p.Port == port);
            }
        }

        [Fact]
        public async Task Proxy_ShouldForwardToTheApiOnly_AndBeTheOnlyAddressTheApiTrusts()
        {
            var caddyfile = await File.ReadAllTextAsync(Path.Combine(ComposeImages.RepositoryRoot, "caddy", "Caddyfile"), TestContext.Current.CancellationToken);
            var upstreams = Regex.Matches(caddyfile, @"^\s*reverse_proxy\s+(?<upstreams>[^{\r\n]+)", RegexOptions.Multiline)
                .SelectMany(m => m.Groups["upstreams"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .ToList();

            Assert.Equal(new[] { "api:8080" }, upstreams);

            var services = (await ComposeConfigAsync(local: false)).GetProperty("services");
            var caddyAddress = services.GetProperty("caddy").GetProperty("networks").GetProperty("default")
                .GetProperty("ipv4_address").GetString();
            var trusted = services.GetProperty("api").GetProperty("environment").EnumerateObject()
                .Where(e => e.Name.StartsWith("ReverseProxy__TrustedProxies__", StringComparison.Ordinal))
                .Select(e => e.Value.GetString())
                .ToList();

            Assert.Equal(new[] { caddyAddress }, trusted);
        }

        // On a developer's machine everything but the proxy is published, on this machine only.
        [Fact]
        public async Task Local_ShouldPublishOnThisMachineOnly_ExceptTheProxy()
        {
            var published = PublishedPorts(await ComposeConfigAsync(local: true));

            Assert.All(published.Where(p => p.Service != "caddy"), p =>
                Assert.True(p.HostIp == "127.0.0.1", $"{p.Service} publishes {p.Port} on '{p.HostIp}'."));
            Assert.Contains(published, p => p.Service == "rabbitmq" && p.Port == "15672");
        }
    }
}
