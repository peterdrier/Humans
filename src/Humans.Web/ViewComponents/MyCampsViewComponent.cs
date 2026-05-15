using System.Security.Claims;
using Humans.Application.Interfaces.Camps;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

/// <summary>
/// Lists camps the current human belongs to (or has requested), grouped by year.
/// Rendered on the human's own profile page. Private — never shown publicly.
/// </summary>
public class MyCampsViewComponent : ViewComponent
{
    private readonly ICampService _campService;
    private readonly ILogger<MyCampsViewComponent> _logger;

    public MyCampsViewComponent(
        ICampService campService,
        ILogger<MyCampsViewComponent> logger)
    {
        _campService = campService;
        _logger = logger;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        try
        {
            if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                return Content(string.Empty);

            var memberships = await _campService.GetCampMembershipsForUserAsync(userId);
            if (memberships.Count == 0)
                return Content(string.Empty);

            var byYear = memberships
                .GroupBy(m => m.Year)
                .OrderByDescending(g => g.Key)
                .Select(g => new MyCampsYearGroup
                {
                    Year = g.Key,
                    Memberships = g.Select(m => new MyCampsMembership
                    {
                        CampSlug = m.CampSlug,
                        CampName = m.CampName,
                        Status = m.Status
                    }).ToList()
                })
                .ToList();

            return View(new MyCampsViewModel { Years = byYear });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load camp memberships for current user");
            return Content(string.Empty);
        }
    }
}

public class MyCampsViewModel
{
    public List<MyCampsYearGroup> Years { get; set; } = [];
}

public class MyCampsYearGroup
{
    public int Year { get; set; }
    public List<MyCampsMembership> Memberships { get; set; } = [];
}

public class MyCampsMembership
{
    public string CampSlug { get; set; } = string.Empty;
    public string CampName { get; set; } = string.Empty;
    public CampMemberStatus Status { get; set; }
}
