using Humans.Calendar.Contracts;
using Humans.Workgroups.Domain;
using NodaTime;

namespace Humans.Workgroups.Services.Contributors;

/// <summary>
/// Workgroups' half of Calendar's fan-out (design §8): a member's own meetings on their
/// personal feed, and every public meeting on the community calendar. Workgroups owns
/// <c>workgroup_meetings</c>; Calendar never reads them.
/// </summary>
internal sealed class WorkgroupCalendarContributor(IWorkgroupService workgroups) : ICalendarFeedContributor
{
    /// <summary>The ICS CATEGORIES value and the badge in Calendar's admin widget.</summary>
    private const string SourceName = "Workgroups";

    public async Task<IReadOnlyList<CalendarFeedItem>> GetCalendarItemsForUserAsync(
        Guid userId, CancellationToken ct)
    {
        var register = await workgroups.GetRegisterAsync(ct);

        // A dormant group's meetings are history, not something to put on a calendar.
        return register
            .Where(w => w.Status == WorkgroupStatus.Active && w.IsMember(userId))
            .SelectMany(w => w.Meetings.Select(m => ToItem(w, m)))
            .ToList();
    }

    public async Task<IReadOnlyList<CalendarFeedItem>> GetPublicItemsForWindowAsync(
        Instant from, Instant to, CancellationToken ct)
    {
        var register = await workgroups.GetRegisterAsync(ct);

        return register
            .Where(w => w.Status == WorkgroupStatus.Active)
            .SelectMany(w => w.Meetings
                .Where(m => m.IsPublic && m.StartUtc < to && m.EndUtc > from)
                .Select(m => ToItem(w, m)))
            .ToList();
    }

    private static CalendarFeedItem ToItem(WorkgroupInfo w, WorkgroupMeetingInfo m) => new(
        $"workgroup-meeting-{m.Id}@humans.nobodies.team",
        SourceName,
        $"{w.Name}: {m.Title}",
        w.Deliverable,
        m.StartUtc,
        m.EndUtc,
        m.Location,
        $"{CalendarFeedItem.BaseUrl}/Workgroups/{w.Slug}");
}
