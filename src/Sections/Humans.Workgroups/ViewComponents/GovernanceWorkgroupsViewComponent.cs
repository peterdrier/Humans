using Humans.Users.Contracts;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Services;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Workgroups.ViewComponents;

/// <summary>
/// The register's card on the Governance page (design §16): every Active group, its
/// coordinator(s), the deliverable sentence, its next meeting, and the same badges the
/// register itself shows. Renders nothing when there is nothing Active — Governance
/// takes no space for a switched-off or empty Workgroups.
/// </summary>
/// <remarks>
/// Internal — invoked by name through the Shell's slot renderer
/// (<c>Component.InvokeAsync</c>), never as a <c>&lt;vc:&gt;</c> tag helper, so Razor's
/// public-only tag-helper discovery does not apply. Public would not compile: the
/// constructor takes the section-internal <c>IWorkgroupService</c> (CS0051).
/// </remarks>
internal sealed class GovernanceWorkgroupsViewComponent(
    IWorkgroupService workgroups, IUserServiceRead users, IClock clock) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var register = await workgroups.GetRegisterAsync();
        var active = register
            .Where(w => w.Status == WorkgroupStatus.Active)
            .OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (active.Count == 0)
            return Content(string.Empty);

        var ids = active.SelectMany(w => w.CoordinatorUserIds()).Distinct().ToList();
        var people = ids.Count == 0
            ? new Dictionary<Guid, UserInfo>()
            : await users.GetUserInfosAsync(ids);

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
