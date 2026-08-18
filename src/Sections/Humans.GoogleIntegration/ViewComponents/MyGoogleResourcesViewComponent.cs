using Humans.GoogleIntegration.Contracts;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace Humans.GoogleIntegration.ViewComponents;

public sealed class MyGoogleResourcesViewComponent(
    ITeamResourceService teamResourceService,
    ILogger<MyGoogleResourcesViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        try
        {
            if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                return Content(string.Empty);

            var resources = await teamResourceService.GetUserTeamResourcesAsync(userId);

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
            logger.LogError(ex, "Failed to load Google resources for current user");
            return Content(string.Empty);
        }
    }
}

internal sealed class MyGoogleResourcesViewModel
{
    public List<MyGoogleResourceWithTeam> Resources { get; set; } = [];
}

internal sealed class MyGoogleResourceWithTeam
{
    public string TeamName { get; set; } = string.Empty;
    public string TeamSlug { get; set; } = string.Empty;
    public MyGoogleResourceItem Resource { get; set; } = null!;
}

internal sealed class MyGoogleResourceItem
{
    public string Name { get; set; } = string.Empty;
    public GoogleResourceType ResourceType { get; set; }
    public string? Url { get; set; }
}
