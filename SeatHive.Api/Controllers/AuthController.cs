using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SeatHive.Api.Models;
using SeatHive.Api.Services;

namespace SeatHive.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public class AuthController : ControllerBase
    {
        private readonly AuthService _authService;

        public AuthController(AuthService authService)
        {
            _authService = authService;
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest request)
        {
            var result = await _authService.RegisterAsync(request.Email, request.Password);
            if (result == RegisterResult.EmailAlreadyRegistered)
            {
                return this.ProblemWithCode(StatusCodes.Status409Conflict, ErrorCodes.EmailAlreadyRegistered, "User already exists.");
            }

            return Ok("User registered successfully.");
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            var token = await _authService.LoginAsync(request.Email, request.Password);
            if (token == null)
            {
                return this.ProblemWithCode(StatusCodes.Status401Unauthorized, ErrorCodes.InvalidCredentials, "Invalid email or password.");
            }

            return Ok(new { Token = token });
        }
    }
}
