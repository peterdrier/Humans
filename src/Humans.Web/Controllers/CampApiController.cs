using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

[AllowAnonymous]
[ApiController]
[Route("api/barrios")]
[Route("api/camps")]
public class CampApiController : ControllerBase
{
    private readonly ICampService _campService;

    public CampApiController(ICampService campService)
    {
        _campService = campService;
    }

    [HttpGet("{year:int}")]
    public async Task<IActionResult> GetCamps(int year)
    {
        var camps = await _campService.GetCampsForYearAsync(year);

        var result = camps.Select(b =>
        {
            var season = b.Seasons.FirstOrDefault(s => s.Year == year);
            var firstImage = b.Images.OrderBy(i => i.SortOrder).FirstOrDefault();
            return new
            {
                b.Id,
                b.Slug,
                Name = season?.Name ?? b.Slug,
                BlurbShort = season?.BlurbShort ?? string.Empty,
                BlurbLong = season?.BlurbLong ?? string.Empty,
                ImageUrl = firstImage != null ? $"/{firstImage.StoragePath}" : (string?)null,
                Vibes = (season?.Vibes ?? new List<CampVibe>()).Select(v => v.ToString()).ToList(),
                AcceptingMembers = (season?.AcceptingMembers ?? YesNoMaybe.No).ToString(),
                KidsWelcome = (season?.KidsWelcome ?? YesNoMaybe.No).ToString(),
                SoundZone = season?.SoundZone?.ToString(),
                Status = (season?.Status ?? CampSeasonStatus.Pending).ToString(),
                b.TimesAtNowhere,
                b.IsSwissCamp,
                b.Links,
                b.WebOrSocialUrl
            };
        }).OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();

        return Ok(result);
    }

    [HttpGet("{year:int}/placement")]
    public async Task<IActionResult> GetPlacement(int year)
    {
        var camps = await _campService.GetCampsForYearAsync(year);

        var result = camps
            .Select(b =>
            {
                var season = b.Seasons.FirstOrDefault(s => s.Year == year);
                if (season is null) return null;
                return new
                {
                    b.Id,
                    b.Slug,
                    season.Name,
                    season.MemberCount,
                    SpaceRequirement = season.SpaceRequirement?.ToString(),
                    SoundZone = season.SoundZone?.ToString(),
                    season.ContainerCount,
                    season.ContainerNotes,
                    ElectricalGrid = season.ElectricalGrid?.ToString(),
                    Status = season.Status.ToString()
                };
            })
            .Where(x => x is not null)
            .OrderBy(x => x!.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(result);
    }
}
