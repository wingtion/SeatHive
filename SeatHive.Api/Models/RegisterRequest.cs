using System.ComponentModel.DataAnnotations;

namespace SeatHive.Api.Models
{
    public class RegisterRequest
    {
        [Required, EmailAddress, MaxLength(254)]
        public string Email { get; set; } = string.Empty;

        // BCrypt only uses the first 72 bytes, so anything longer is rejected instead of silently cut.
        [Required, MinLength(8), MaxUtf8Bytes(72)]
        public string Password { get; set; } = string.Empty;
    }
}
