using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using SeatHive.Api.Models;
using SeatHive.Api.Services;

namespace SeatHive.Api.Data
{
    public static class AdminSeeder
    {
        public const string EmailKey = "SEATHIVE_ADMIN_EMAIL";
        public const string PasswordKey = "SEATHIVE_ADMIN_PASSWORD";

        // Creates the admin from configuration. Does nothing when the two settings are not set.
        public static async Task SeedAsync(AppDbContext db, IConfiguration configuration, ILogger logger)
        {
            var email = configuration[EmailKey];
            var password = configuration[PasswordKey];

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                logger.LogInformation("{EmailKey} / {PasswordKey} are not set; no admin user was seeded.", EmailKey, PasswordKey);
                return;
            }

            // Same rules as a normal registration.
            var request = new RegisterRequest { Email = email, Password = password };
            var errors = new List<ValidationResult>();
            if (!Validator.TryValidateObject(request, new ValidationContext(request), errors, validateAllProperties: true))
            {
                throw new InvalidOperationException(
                    $"{EmailKey} / {PasswordKey} are not valid: " + string.Join(" ", errors.Select(e => e.ErrorMessage)));
            }

            var normalizedEmail = AuthService.NormalizeEmail(email);
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);

            if (user == null)
            {
                db.Users.Add(new User
                {
                    Email = normalizedEmail,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                    Role = Roles.Admin
                });
            }
            else if (user.Role != Roles.Admin)
            {
                // An existing account keeps its password and is only promoted.
                user.Role = Roles.Admin;
            }

            await db.SaveChangesAsync();
        }
    }
}
