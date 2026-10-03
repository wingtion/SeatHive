using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SeatHive.Api.Models;
using SeatHive.Api.Services;

namespace SeatHive.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Roles = Roles.Admin)]
    public class SetupController : ControllerBase
    {
        private readonly DemoDataResetter _resetter;

        public SetupController(DemoDataResetter resetter)
        {
            _resetter = resetter;
        }

        [HttpPost("create-data")]
        public async Task<IActionResult> CreateData()
        {
            var result = await _resetter.ResetAsync();

            return Ok(new { Message = "Database setup complete!", SeatsCreated = result.SeatsCreated });
        }
    }
}
