using Humans.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

[Authorize]
[ApiController]
[Route("api/humans")]
public class HumanApiController : ControllerBase
{
    private readonly IProfileService _profileService;

    public HumanApiController(IProfileService profileService)
    {
        _profileService = profileService;
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return Ok(Array.Empty<object>());

        var results = await _profileService.SearchHumansAsync(q);
        return Ok(results.Take(10).Select(r => new
        {
            r.UserId,
            r.DisplayName,
            r.BurnerName
        }));
    }
}
