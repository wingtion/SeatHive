using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using SeatHive.Api.Models;
using SeatHive.Api.Services;

namespace SeatHive.Api.Controllers
{
    // The race simulation, for anyone signed in: racers try to hold one seat at the same moment and exactly one
    // may get it. The work is in RaceSimulator.
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Simulation)]
    public class SimulationController : ControllerBase
    {
        private readonly RaceSimulator _simulator;

        public SimulationController(RaceSimulator simulator)
        {
            _simulator = simulator;
        }

        [HttpPost("simulate-concurrency")]
        public async Task<IActionResult> SimulateConcurrency(
            [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RaceRequest? request)
        {
            var (report, refusal) = await _simulator.RunAsync(request?.SeatId, request?.Racers ?? RaceRequest.DefaultRacers);

            if (refusal != null)
            {
                var problem = this.ProblemWithCode(refusal.Status, refusal.Code, refusal.Title);
                if (refusal.ReleasesAt != null) ((ProblemDetails)problem.Value!).Extensions["releasesAt"] = refusal.ReleasesAt;
                return problem;
            }

            return Ok(report);
        }
    }
}
