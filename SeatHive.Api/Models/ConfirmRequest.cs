namespace SeatHive.Api.Models
{
    // Optional body of the confirm endpoint.
    public class ConfirmRequest
    {
        // Demo switch: the simulated payment for this confirm fails for certain.
        public bool SimulatePaymentFailure { get; set; }
    }
}
