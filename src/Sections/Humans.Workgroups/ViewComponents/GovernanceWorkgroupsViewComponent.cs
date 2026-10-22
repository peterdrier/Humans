using Humans.Users.Contracts;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Services;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Workgroups.ViewComponents;

/// <summary>Renders nothing when no group is Active, so Governance takes no space for an empty register.</summary>
/// <remarks>
/// Internal: invoked by Type through the Shell's slot renderer, never as a tag helper. Public wouldn't compile:
/// the constructor takes the internal <c>IWorkgroupService</c>.
/// </remarks>
internal sealed class GovernanceWorkgroupsViewComponent(
    IWorkgroupService workgroups, IUserService users, IClock clock) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var ct = HttpContext.RequestAborted;
        var register = await workgroups.GetRegisterAsync(ct);
        // Everyone reads this card, so a sensitive group shows its register entry only: no next meeting and no
        // comment or update-due badge.
        var active = register
            .Where(w => w.Status == WorkgroupStatus.Active)
            .Select(w => w.IsSensitive ? w with { Meetings = [], Documents = [] } : w)
            .OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (active.Count == 0)
            return Content(string.Empty);

        var ids = active.SelectMany(w => w.CoordinatorUserIds()).Distinct().ToList();
        var people = ids.Count == 0
            ? new Dictionary<Guid, UserInfo>()
            : await users.GetUserInfosAsync(ids, ct);

        return View(new GovernanceWorkgroupsViewModel(active, people, clock.GetCurrentInstant()));
    }
}

internal sealed record GovernanceWorkgroupsViewModel(
    IReadOnlyList<WorkgroupInfo> Active,
    IReadOnlyDictionary<Guid, UserInfo> People,
    Instant Now)
{
    public string DisplayName(Guid userId) =>
        People.TryGetValue(userId, out var info) ? info.BurnerName : "—";
}
