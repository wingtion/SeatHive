using System.ComponentModel.DataAnnotations;
using SeatHive.Api.Services;

namespace SeatHive.Api.Models
{
    public class RegisterRequest : IValidatableObject
    {
        [Required, EmailAddress, MaxLength(254)]
        public string Email { get; set; } = string.Empty;

        // BCrypt only uses the first 72 bytes, so anything longer is rejected instead of silently cut.
        [Required, MinLength(8), MaxUtf8Bytes(72)]
        public string Password { get; set; } = string.Empty;

        // The racers of the race simulation have this domain; nobody may register in their place.
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (RacerAccounts.IsRacerEmail(Email))
            {
                yield return new ValidationResult(
                    $"Emails at {RacerAccounts.Domain} are reserved for the race simulation.", new[] { nameof(Email) });
            }
        }
    }
}
