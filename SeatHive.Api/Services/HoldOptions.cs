namespace SeatHive.Api.Services
{
    // Bound to the "Holds" configuration section.
    public class HoldOptions
    {
        public const string SectionName = "Holds";

        // How long a held seat is reserved for its user before it expires.
        public int DurationSeconds { get; set; } = 300;

        // How many seats one user can hold at the same time.
        public int MaxActivePerUser { get; set; } = 4;

        // A booking waiting for its payment result is kept this long after its hold ran out,
        // so a payment made in time is not lost because the result arrived a moment late.
        public int PaymentGraceSeconds { get; set; } = 30;

        // How often the background sweeper expires holds that ran out.
        public int SweepIntervalSeconds { get; set; } = 30;
    }
}
