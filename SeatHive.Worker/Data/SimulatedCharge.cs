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

        // When the result of this charge was announced to the booking system. It is announced once; null until then.
        public DateTime? AnnouncedAt { get; set; }

        // When the charge was given back. A charge is given back at most once; null until then.
        public DateTime? RefundedAt { get; set; }
    }
}
