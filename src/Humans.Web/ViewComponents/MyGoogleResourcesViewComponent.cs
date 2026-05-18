using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.GoogleIntegration;

namespace Humans.Web.ViewComponents;

public class MyGoogleResourcesViewComponent : ViewComponent
{
    private readonly ITeamResourceService _teamResourceService;
    private readonly ILogger<MyGoogleResourcesViewComponent> _logger;

    public MyGoogleResourcesViewComponent(
        ITeamResourceService teamResourceService,
        ILogger<MyGoogleResourcesViewComponent> logger)
    {
        _teamResourceService = teamResourceService;
        _logger = logger;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        try
        {
            if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                return Content(string.Empty);

            var resources = await _teamResourceService.GetUserTeamResourcesAsync(userId);

            if (resources.Count == 0)
                return Content(string.Empty);

            var model = new MyGoogleResourcesViewModel
            {
                Resources = resources.Select(r => new MyGoogleResourceWithTeam
                {
                    TeamName = r.TeamName,
                    TeamSlug = r.TeamSlug,
                    Resource = new MyGoogleResourceItem
                    {
                        Name = r.ResourceName,
                        ResourceType = r.ResourceType,
                        Url = r.Url
                    }
                }).ToList()
            };

            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Google resources for current user");
            return Content(string.Empty);
        }
    }
}

public class MyGoogleResourcesViewModel
{
    public List<MyGoogleResourceWithTeam> Resources { get; set; } = [];
}

public class MyGoogleResourceWithTeam
{
    public string TeamName { get; set; } = string.Empty;
    public string TeamSlug { get; set; } = string.Empty;
    public MyGoogleResourceItem Resource { get; set; } = null!;
}

public class MyGoogleResourceItem
{
    public string Name { get; set; } = string.Empty;
    public GoogleResourceType ResourceType { get; set; }
    public string? Url { get; set; }
}
