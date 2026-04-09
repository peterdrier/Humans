using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Web.ViewComponents;

public class MyGoogleResourcesViewComponent : ViewComponent
{
    private readonly ITeamService _teamService;
    private readonly UserManager<User> _userManager;
    private readonly ILogger<MyGoogleResourcesViewComponent> _logger;

    public MyGoogleResourcesViewComponent(
        ITeamService teamService,
        UserManager<User> userManager,
        ILogger<MyGoogleResourcesViewComponent> logger)
    {
        _teamService = teamService;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        try
        {
            var user = await _userManager.GetUserAsync(UserClaimsPrincipal);
            if (user is null)
                return Content(string.Empty);

            var resources = await _teamService.GetUserTeamGoogleResourcesAsync(user.Id);

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
