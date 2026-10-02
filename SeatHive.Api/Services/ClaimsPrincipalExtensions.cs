using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace SeatHive.Api.Services
{
    public static class ClaimsPrincipalExtensions
    {
        // The one place that reads the user id from the token ("sub").
        public static bool TryGetUserId(this ClaimsPrincipal user, out int userId)
        {
            return int.TryParse(user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out userId);
        }
    }
}
