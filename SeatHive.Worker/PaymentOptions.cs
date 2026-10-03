namespace SeatHive.Worker
{
    // Bound to the "Payment" configuration section. There is no real payment provider; everything here is simulated.
    public class PaymentOptions
    {
        public const string SectionName = "Payment";

        // The share of payments that fail at random, from 0 (never) to 1 (always).
        public double FailureRate { get; set; } = 0.2;

        // A payment takes a random time between these two.
        public int MinDelayMs { get; set; } = 1000;
        public int MaxDelayMs { get; set; } = 3000;

        // How long the provider remembers a charge. After that the same idempotency key would be charged again,
        // so this must stay far longer than a payment request can keep coming back from the broker.
        public int ChargeRetentionDays { get; set; } = 7;

        // How often charges older than that are deleted.
        public int CleanupIntervalMinutes { get; set; } = 60;
    }
}
