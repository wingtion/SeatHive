using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace SeatHive.Api.Services
{
    // A guest is a real user that a visitor gets with one request, without an email or a password, so everyone has
    // bookings, limits and a history of their own. Nobody can sign in as a guest: the password hash is made from
    // a secret that is thrown away. Nobody can register one either: the domain is refused.
    public static class GuestAccounts
    {
        public const string Domain = "guests.seathive.invalid";

        // A guest's token is accepted this long (its lifetime plus the clock skew the validation allows).
        // A guest is kept at least that long, so a token that still works always has its user.
        public static readonly TimeSpan KeptFor = AuthService.TokenLifetime + TokenValidationParameters.DefaultClockSkew;

        public static string NewEmail() => $"guest-{Guid.NewGuid():N}@{Domain}";

        public static bool IsGuestEmail(string email) =>
            email.Trim().EndsWith("@" + Domain, StringComparison.OrdinalIgnoreCase);

        private static readonly Lazy<string> UnusableHash = new(() =>
            BCrypt.Net.BCrypt.HashPassword(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), workFactor: 4));

        public static string UnusablePasswordHash => UnusableHash.Value;
    }
}
