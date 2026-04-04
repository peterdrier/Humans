using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Web.ViewComponents;

public class MyGoogleResourcesViewComponent : ViewComponent
{
    private readonly HumansDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly ILogger<MyGoogleResourcesViewComponent> _logger;

    public MyGoogleResourcesViewComponent(
        HumansDbContext dbContext,
        UserManager<User> userManager,
        ILogger<MyGoogleResourcesViewComponent> logger)
    {
        _dbContext = dbContext;
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

            // Get the user's active team memberships with their Google resources
            var teamResources = await _dbContext.TeamMembers
                .AsNoTracking()
                .Where(tm => tm.UserId == user.Id && tm.LeftAt == null)
                .Select(tm => new
                {
                    TeamName = tm.Team.Name,
                    TeamSlug = tm.Team.Slug,
                    Resources = tm.Team.GoogleResources
                        .Where(r => r.IsActive)
                        .Select(r => new MyGoogleResourceItem
                        {
                            Name = r.Name,
                            ResourceType = r.ResourceType,
                            Url = r.Url
                        })
                        .ToList()
                })
                .Where(t => t.Resources.Count > 0)
                .OrderBy(t => t.TeamName)
                .ToListAsync();

            if (teamResources.Count == 0)
                return Content(string.Empty);

            var model = new MyGoogleResourcesViewModel();

            foreach (var team in teamResources)
            {
                foreach (var resource in team.Resources)
                {
                    model.Resources.Add(new MyGoogleResourceWithTeam
                    {
                        TeamName = team.TeamName,
                        TeamSlug = team.TeamSlug,
                        Resource = resource
                    });
                }
            }

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
