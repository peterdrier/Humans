using Humans.Application.Interfaces.Profiles;
using Humans.Application.Services.Profile;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

[Authorize]
[ApiController]
[Route("api/profiles")]
public class ProfileApiController : ControllerBase
{
    private readonly IProfileService _profileService;

    public ProfileApiController(IProfileService profileService)
    {
        _profileService = profileService;
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] string? scope = null)
    {
        if (!q.HasSearchTerm())
            return Ok(Array.Empty<HumanLookupSearchResult>());

        // scope=name → narrowed match on display name + burner name only.
        // anything else (default) → broad match across bio / city / interests / CV / pronouns.
        var predicate = string.Equals(scope, "name", StringComparison.OrdinalIgnoreCase)
            ? ProfileSearchPredicates.ByName(q)
            : ProfileSearchPredicates.Broad(q);
        var results = await _profileService.SearchProfilesAsync(predicate);

        return Ok(results
            .Take(10)
            .Select(r => r.ToHumanLookupSearchResult()));
    }
}
