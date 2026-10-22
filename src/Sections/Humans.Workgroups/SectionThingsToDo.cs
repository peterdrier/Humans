using System.Globalization;
using System.Security.Claims;
using Humans.Base.Interfaces;
using Humans.Workgroups.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Workgroups;

/// <summary>Coordinators only, and only what is outstanding: the list is what needs doing, not what exists.</summary>
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

        foreach (var workgroup in coordinated.Where(w => w.PendingMembers().Any()))
        {
            entries.Add(new ThingsToDoEntry($"workgroup-requests-{workgroup.Id}",
                localizer["Workgroups_Todo_MembershipRequests_Title"].Value,
                "fa-solid fa-user-plus",
                RawHref: $"/Workgroups/{workgroup.Slug}", Weight: 40)
            {
                Description = string.Format(CultureInfo.CurrentCulture,
                    localizer["Workgroups_Todo_MembershipRequests_Description"].Value,
                    workgroup.Name, workgroup.PendingMembers().Count()),
                ActionText = localizer["Workgroups_Todo_MembershipRequests_Action"].Value,
            });
        }

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

        return entries;
    }
}
