using Humans.Calendar.Contracts;
using Humans.Workgroups.Domain;
using NodaTime;

namespace Humans.Workgroups.Services.Contributors;

internal sealed class WorkgroupCalendarContributor(IWorkgroupService workgroups) : ICalendarFeedContributor
{
    private const string SourceName = "Workgroups";

    public async Task<IReadOnlyList<CalendarFeedItem>> GetCalendarItemsForUserAsync(
        Guid userId, CancellationToken ct)
    {
        var register = await workgroups.GetRegisterAsync(ct);

        // A dormant group's meetings are history, not calendar entries.
        return register
            .Where(w => w.Status == WorkgroupStatus.Active && w.IsMember(userId))
            .SelectMany(w => w.Meetings.Select(m => ToItem(w, m)))
            .ToList();
    }

    public async Task<IReadOnlyList<CalendarFeedItem>> GetPublicItemsForWindowAsync(
        Instant from, Instant to, CancellationToken ct)
    {
        var register = await workgroups.GetRegisterAsync(ct);

        // A sensitive group's meetings are its members' business; their personal feed still carries them.
        return register
            .Where(w => w.Status == WorkgroupStatus.Active && !w.IsSensitive)
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
        $"/Workgroups/{w.Slug}");
}
