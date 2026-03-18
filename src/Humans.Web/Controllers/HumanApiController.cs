using Humans.Application.Interfaces;
using Humans.Web.Extensions;
using Humans.Web.Models;
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
        if (!q.HasSearchTerm())
            return Ok(Array.Empty<HumanLookupSearchResult>());

        var results = await _profileService.SearchHumansAsync(q);
        return Ok(results
            .Take(10)
            .Select(r => r.ToHumanLookupSearchResult()));
    }
}
