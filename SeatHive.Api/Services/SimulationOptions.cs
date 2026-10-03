namespace SeatHive.Api.Services
{
    // Bound to the "Simulation" configuration section.
    public class SimulationOptions
    {
        public const string SectionName = "Simulation";

        // How long the winner of a race keeps the seat before it is released, so the hold can be seen.
        public int WinnerHoldSeconds { get; set; } = 10;
    }
}
