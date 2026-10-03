using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
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
        private readonly IConfiguration _configuration;

        public AuthService(AppDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
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

        // 3. GENERATE TOKEN
        private string GenerateJwtToken(User user)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]!));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()), // Store User ID in the token
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim("role", user.Role),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            var token = new JwtSecurityToken(
                issuer: _configuration["Jwt:Issuer"],
                audience: _configuration["Jwt:Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddHours(2), // Token valid for 2 hours
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
