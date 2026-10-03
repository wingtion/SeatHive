namespace SeatHive.Api.Services
{
    // Bound to the "Jwt" configuration section.
    public class JwtOptions
    {
        public const string SectionName = "Jwt";

        // The signing key. A secret: it is never in appsettings.json, and startup stops without it (see Program.cs).
        public string Key { get; set; } = string.Empty;

        // Written into every token and checked on every request.
        public string? Issuer { get; set; }

        public string? Audience { get; set; }
    }
}
