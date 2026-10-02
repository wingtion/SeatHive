using System.ComponentModel.DataAnnotations;

namespace SeatHive.Api.Models
{
    public class LoginRequest
    {
        [Required, EmailAddress, MaxLength(254)]
        public string Email { get; set; } = string.Empty;

        [Required, MaxUtf8Bytes(72)]
        public string Password { get; set; } = string.Empty;
    }
}
