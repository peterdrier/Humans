using System.Globalization;
using System.Security.Claims;
using Humans.Base.Interfaces;
using Humans.Workgroups.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Workgroups;

/// <summary>
/// Workgroups' things-to-do entries, for coordinators only: the monthly update the group
/// owes, and a status update somebody asked for and nobody answered. A member with nothing
/// outstanding gets nothing — the list is what needs doing, not what exists.
/// </summary>
internal sealed class SectionThingsToDo : ISectionThingsToDo
{
    public async ValueTask<IEnumerable<ThingsToDoEntry>> EntriesAsync(
        IServiceProvider services, ClaimsPrincipal user)
    {
        if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return [];

        var now = services.GetRequiredService<IClock>().GetCurrentInstant();
        var mine = await services.GetRequiredService<IWorkgroupService>().GetForMemberAsync(userId);
        var coordinated = mine.Where(w => w.CoordinatorUserIds().Contains(userId)).ToList();
        if (coordinated.Count == 0)
            return [];

        var localizer = services.GetRequiredService<IStringLocalizer<WorkgroupsResource>>();
        var entries = new List<ThingsToDoEntry>();

        foreach (var workgroup in coordinated.Where(w => w.IsUpdateDue(now)))
        {
            entries.Add(new ThingsToDoEntry($"workgroup-update-{workgroup.Id}",
                localizer["Workgroups_Todo_UpdateDue_Title"].Value,
                "fa-solid fa-people-group",
                RawHref: $"/Workgroups/{workgroup.Slug}", Weight: 45)
            {
                Description = string.Format(CultureInfo.CurrentCulture,
                    localizer["Workgroups_Todo_UpdateDue_Description"].Value, workgroup.Name),
                ActionText = localizer["Workgroups_Todo_UpdateDue_Action"].Value,
            });
        }

        foreach (var workgroup in coordinated.Where(w => w.UnansweredStatusRequestAt() is not null))
        {
            entries.Add(new ThingsToDoEntry($"workgroup-status-{workgroup.Id}",
                localizer["Workgroups_Todo_StatusRequested_Title"].Value,
                "fa-solid fa-circle-question",
                RawHref: $"/Workgroups/{workgroup.Slug}", Weight: 46,
                Severity: workgroup.IsStatusOverdue(now) ? TileSeverity.Warning : TileSeverity.Normal)
            {
                Description = string.Format(CultureInfo.CurrentCulture,
                    localizer["Workgroups_Todo_StatusRequested_Description"].Value, workgroup.Name),
                ActionText = localizer["Workgroups_Todo_StatusRequested_Action"].Value,
            });
        }

        return entries;
    }
}
