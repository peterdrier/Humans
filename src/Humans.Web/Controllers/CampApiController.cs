using Humans.Application.Interfaces.Camps;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

[AllowAnonymous]
[ApiController]
[EnableCors("BarriosPublic")]
[Route("api/barrios")]
[Route("api/camps")]
public class CampApiController(ICampServiceRead campService) : ControllerBase
{
    [HttpGet("{year:int}")]
    public async Task<IActionResult> GetCamps(int year, CancellationToken cancellationToken)
    {
        var summaries = (await campService.GetCampsForYearAsync(year, cancellationToken))
            .Select(camp => new
            {
                Camp = camp,
                Season = camp.Seasons.FirstOrDefault(season =>
                    season.Year == year &&
                    season.Status is CampSeasonStatus.Active or CampSeasonStatus.Full)
            })
            .Where(row => row.Season is not null)
            .Select(row => new CampPublicSummary(
                row.Camp.Id,
                row.Camp.Slug,
                row.Season!.Name,
                row.Season.BlurbShort,
                row.Season.BlurbLong,
                row.Camp.Images.FirstOrDefault()?.Url,
                row.Season.Vibes.Select(vibe => vibe.ToString()).ToList(),
                row.Season.AcceptingMembers.ToString(),
                row.Season.KidsWelcome.ToString(),
                row.Season.SoundZone?.ToString(),
                row.Season.Status.ToString(),
                row.Camp.TimesAtNowhere,
                row.Camp.IsSwissCamp,
                row.Camp.Links,
                row.Camp.WebOrSocialUrl))
            .OrderBy(summary => summary.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(summaries);
    }

    [HttpGet("{year:int}/placement")]
    public async Task<IActionResult> GetPlacement(int year, CancellationToken cancellationToken)
    {
        var summaries = (await campService.GetCampsForYearAsync(year, cancellationToken))
            .Select(camp => new
            {
                Camp = camp,
                Season = camp.Seasons.FirstOrDefault(season =>
                    season.Year == year &&
                    season.Status is CampSeasonStatus.Active or CampSeasonStatus.Full)
            })
            .Where(row => row.Season is not null)
            .Select(row => new CampPlacementSummary(
                row.Camp.Id,
                row.Camp.Slug,
                row.Season!.Name,
                row.Season.MemberCount,
                row.Season.SpaceRequirement?.ToString(),
                row.Season.SoundZone?.ToString(),
                row.Season.Status.ToString(),
                row.Season.ElectricalGrid?.ToString()))
            .OrderBy(summary => summary.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(summaries);
    }
}
