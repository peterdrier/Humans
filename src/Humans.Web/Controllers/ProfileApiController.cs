using Humans.Application.Interfaces.Profiles;
using Humans.Application.Services.Profiles;
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
    private const int MaxResults = 10;

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
        // anything else (default) → broad match across bio / city / interests / CV +
        // every public ContactField. Admin bit is never set here — services are
        // auth-free, but a non-admin endpoint passing it would be a programmer
        // error caught in code review (see PersonSearchFields docs).
        var fields = string.Equals(scope, "name", StringComparison.OrdinalIgnoreCase)
            ? PersonSearchFields.Name
            : PersonSearchFields.PublicAll;

        var results = await _profileService.SearchProfilesAsync(q, fields, MaxResults);

        // Display ordering belongs at the presentation layer, per
        // memory/architecture/display-sort-in-controllers.md.
        return Ok(results
            .OrderBy(r => r.BurnerName, StringComparer.OrdinalIgnoreCase)
            .Select(r => new HumanLookupSearchResult(r.UserId, r.BurnerName)));
    }
}
