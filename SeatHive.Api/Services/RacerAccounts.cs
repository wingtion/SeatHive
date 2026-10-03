using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SeatHive.Api.Data;
using SeatHive.Api.Models;

namespace SeatHive.Api.Services
{
    // The racers of the race simulation are real users, one per racer, because a booking belongs to a user and the
    // per-user row lock would line up attempts made by one user. Nobody can sign in as a racer: the password hash
    // is made from a secret that is thrown away. Nobody can register one either: the domain is refused.
    public static class RacerAccounts
    {
        public const int Count = 50;
        public const string Domain = "racers.seathive.invalid";

        public static string EmailOf(int racer) => $"racer-{racer:D2}@{Domain}";

        // The racer's number from its email, for the race report (which never shows emails or user ids).
        public static int NumberOf(string email) => int.Parse(email.AsSpan(6, 2));

        public static bool IsRacerEmail(string email) =>
            email.Trim().EndsWith("@" + Domain, StringComparison.OrdinalIgnoreCase);

        private static readonly Lazy<string> UnusableHash = new(() =>
            BCrypt.Net.BCrypt.HashPassword(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), workFactor: 4));

        // Creates the racers that do not exist yet. Safe to run concurrently: an existing email is left alone.
        public static async Task EnsureAsync(AppDbContext db, CancellationToken cancellationToken = default)
        {
            if (await db.Users.CountAsync(u => u.Role == Roles.Racer, cancellationToken) >= Count) return;

            var hash = UnusableHash.Value;
            for (var racer = 1; racer <= Count; racer++)
            {
                var email = EmailOf(racer);
                await db.Database.ExecuteSqlAsync(
                    $"INSERT INTO \"Users\" (\"Email\", \"PasswordHash\", \"Role\") VALUES ({email}, {hash}, {Roles.Racer}) ON CONFLICT (\"Email\") DO NOTHING",
                    cancellationToken);
            }
        }
    }
}
