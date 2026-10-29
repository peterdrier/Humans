using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Camps.Contracts;

/// <summary>Private: shown only on the human's own profile page, never publicly.</summary>
public class MyCampsViewComponent(ICampService campService, ILogger<MyCampsViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var ct = HttpContext.RequestAborted;
        try
        {
            if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                return Content(string.Empty);

            var settings = await campService.GetSettingsAsync(ct);
            // Open events plus the active one, latest gate opening first.
            var events = settings.Events
                .Where(e => e.RegistrationOpen || e.EventSettingsId == settings.ActiveEventSettingsId)
                .ToList();

            var groups = new List<MyCampsEventGroup>();
            foreach (var campEvent in events)
            {
                var camps = await campService.GetCampsForEventAsync(campEvent.EventSettingsId, ct);
                var memberships = camps
                    .SelectMany(camp => camp.Seasons
                        .SelectMany(season => season.Members
                            .Where(member => member.UserId == userId)
                            .Select(member => new MyCampsMembership
                            {
                                CampSlug = camp.Slug,
                                CampName = season.Name,
                                Status = member.Status
                            })))
                    .OrderBy(m => m.CampName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (memberships.Count > 0)
                    groups.Add(new MyCampsEventGroup { EventName = campEvent.EventName, Memberships = memberships });
            }

            if (groups.Count == 0)
                return Content(string.Empty);

            return View(new MyCampsViewModel { Events = groups });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load camp memberships for current user");
            return Content(string.Empty);
        }
    }
}

public class MyCampsViewModel
{
    public List<MyCampsEventGroup> Events { get; set; } = [];
}

public class MyCampsEventGroup
{
    public string EventName { get; set; } = string.Empty;
    public List<MyCampsMembership> Memberships { get; set; } = [];
}

public class MyCampsMembership
{
    public string CampSlug { get; set; } = string.Empty;
    public string CampName { get; set; } = string.Empty;
    public CampMemberStatus Status { get; set; }
}
