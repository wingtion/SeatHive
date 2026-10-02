using System.ComponentModel.DataAnnotations;

namespace SeatHive.Api.Models
{
    public class BookingRequest
    {
        [Range(1, int.MaxValue)]
        public int SeatId { get; set; }
    }
}
