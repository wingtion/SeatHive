using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using SeatHive.Api.Data;
using SeatHive.Api.Models;

namespace SeatHive.Api.Services
{
    public enum RegisterResult
    {
        Registered,
        EmailAlreadyRegistered
    }

    public class AuthService
    {
        private readonly AppDbContext _context;
        // How long a token is valid.
        public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(2);

        private readonly JwtOptions _jwt;
        private readonly TimeProvider _timeProvider;

        public AuthService(AppDbContext context, IOptions<JwtOptions> jwt, TimeProvider timeProvider)
        {
            _context = context;
            _jwt = jwt.Value;
            _timeProvider = timeProvider;
        }

        // Emails are stored and compared in lowercase.
        public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

        // 1. REGISTER
        public async Task<RegisterResult> RegisterAsync(string email, string password)
        {
            email = NormalizeEmail(email);

            if (await _context.Users.AnyAsync(u => u.Email == email))
                return RegisterResult.EmailAlreadyRegistered;

            var user = new User
            {
                Email = email,
                // Hash the password using BCrypt
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password)
            };

            _context.Users.Add(user);

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Another request registered the same email between our check and our insert.
                return RegisterResult.EmailAlreadyRegistered;
            }

            return RegisterResult.Registered;
        }

        // 2. LOGIN
        public async Task<string?> LoginAsync(string email, string password)
        {
            email = NormalizeEmail(email);
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == email);

            // Check if user exists AND if password matches the hash
            if (user == null || !BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            {
                return null; // Login failed
            }

            // Generate JWT Token
            return GenerateJwtToken(user);
        }

        // 3. GUEST: a new user of its own for a visitor, signed in right away (see GuestAccounts)
        public async Task<string> CreateGuestAsync()
        {
            var guest = new User
            {
                Email = GuestAccounts.NewEmail(),
                PasswordHash = GuestAccounts.UnusablePasswordHash,
                Role = Roles.Guest,
                // The application's clock, not the database's: a reset removes old guests by this, on the same clock.
                CreatedAt = _timeProvider.GetUtcNow().UtcDateTime
            };

            _context.Users.Add(guest);
            await _context.SaveChangesAsync();

            return GenerateJwtToken(guest);
        }

        // 4. GENERATE TOKEN
        private string GenerateJwtToken(User user)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Key));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()), // Store User ID in the token
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim("role", user.Role),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            var token = new JwtSecurityToken(
                issuer: _jwt.Issuer,
                audience: _jwt.Audience,
                claims: claims,
                expires: DateTime.UtcNow.Add(TokenLifetime),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
