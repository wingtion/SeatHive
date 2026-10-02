namespace SeatHive.Worker.Data
{
    // What the simulated payment provider remembers about one charge.
    // It stands in for the records a real provider keeps on its own side.
    public class SimulatedCharge
    {
        // One payment attempt. Asking again with the same key returns this charge instead of charging again.
        public Guid IdempotencyKey { get; set; }

        public bool Succeeded { get; set; }
        public string? Reason { get; set; }
        public DateTime ChargedAt { get; set; }
    }
}
