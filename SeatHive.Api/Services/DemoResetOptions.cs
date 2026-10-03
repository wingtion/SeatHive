namespace SeatHive.Api.Services
{
    // Bound to the "DemoReset" configuration section.
    public class DemoResetOptions
    {
        public const string SectionName = "DemoReset";

        // The time of day (UTC, "HH:mm") at which the demo data is reset every night.
        // Not set: there is no nightly reset, only the admin's.
        public string? DailyAtUtc { get; set; }
    }
}
