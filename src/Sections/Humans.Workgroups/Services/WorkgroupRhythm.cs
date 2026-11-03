using Humans.Calendar.Contracts;
using Humans.Workgroups.Domain;
using NodaTime;

namespace Humans.Workgroups.Services;

/// <summary>
/// Kept out of the projection: the register is cached, so anything derived from "now" is computed at read time.
/// The job and the views both ask here, so a page badge and a job action can't disagree.
/// </summary>
internal static class WorkgroupRhythm
{
    public static readonly Duration UpdateDueAfter = Duration.FromDays(30);

    /// <summary>Clause 1's fourteen days for the Secretary to act on an application.</summary>
    public static readonly Duration RegistrationDueAfter = Duration.FromDays(14);

    /// <summary>A delivered document with no disposition after this long is highlighted for the Board.</summary>
    public static readonly Duration DispositionOverdueAfter = Duration.FromDays(60);

    /// <summary>A soft leave keeps the row off the roster; a pending request isn't on it yet.</summary>
    public static IEnumerable<WorkgroupMemberInfo> CurrentMembers(this WorkgroupInfo w) =>
        w.Members.Where(m => m.LeftAt is null && !m.IsPending);

    public static IEnumerable<WorkgroupMemberInfo> PendingMembers(this WorkgroupInfo w) =>
        w.Members.Where(m => m.LeftAt is null && m.IsPending);

    public static bool IsPendingMember(this WorkgroupInfo w, Guid userId) =>
        w.PendingMembers().Any(m => m.UserId == userId);

    public static bool IsCoordinator(this WorkgroupInfo w, Guid userId) =>
        w.Coordinators().Any(m => m.UserId == userId);

    public static IEnumerable<WorkgroupMemberInfo> Coordinators(this WorkgroupInfo w) =>
        w.CurrentMembers().Where(m => m.Role == WorkgroupMemberRole.Coordinator);

    public static IReadOnlyList<Guid> CoordinatorUserIds(this WorkgroupInfo w) =>
        w.Coordinators().Select(m => m.UserId).ToList();

    public static IReadOnlyList<Guid> CurrentMemberUserIds(this WorkgroupInfo w) =>
        w.CurrentMembers().Select(m => m.UserId).ToList();

    public static bool IsMember(this WorkgroupInfo w, Guid userId) =>
        w.CurrentMembers().Any(m => m.UserId == userId);

    public static bool AcceptsMemberWork(this WorkgroupInfo w) => w.Status == WorkgroupStatus.Active;

    /// <summary>
    /// Each occurrence of <paramref name="m"/> starting in [<paramref name="from"/>, <paramref name="to"/>), as the
    /// meeting with that occurrence's times. A one-off meeting is its own single occurrence.
    /// </summary>
    public static IEnumerable<WorkgroupMeetingInfo> Occurrences(this WorkgroupMeetingInfo m, Instant from, Instant to) =>
        CalendarRecurrence.Occurrences(m.StartUtc, m.EndUtc, m.RecurrenceRule, m.RecurrenceTimezone, from, to)
            .Where(o => o.Start >= from)
            .Select(o => m with { StartUtc = o.Start, EndUtc = o.End });

    /// <summary>
    /// The first occurrence starting at or after <paramref name="now"/>, however far off; null once a series has
    /// ended. The window doubles from a year, so a dense rule stops at the first and a sparse one is still found.
    /// </summary>
    public static WorkgroupMeetingInfo? NextOccurrence(this WorkgroupMeetingInfo m, Instant now)
    {
        if (m.StartUtc >= now) return m;
        if (m.RecurrenceRule is null) return null;
        for (var days = 366; days <= 366 * 128; days *= 2)
        {
            if (m.Occurrences(now, now + Duration.FromDays(days)).FirstOrDefault() is { } next) return next;
        }
        return null;
    }

    /// <summary>Every occurrence that has started, plus each meeting's next one: what the timeline shows.</summary>
    public static IEnumerable<WorkgroupMeetingInfo> MeetingOccurrences(this WorkgroupInfo w, Instant now) =>
        w.Meetings.SelectMany(m => m.Occurrences(m.StartUtc, now)
            .Concat(m.NextOccurrence(now) is { } next ? [next] : []));

    public static WorkgroupMeetingInfo? NextMeeting(this WorkgroupInfo w, Instant now) =>
        w.Meetings.Select(m => m.NextOccurrence(now))
            .OfType<WorkgroupMeetingInfo>()
            .MinBy(m => m.StartUtc);

    /// <summary>
    /// An Update, meeting notes, or a meeting's stored start; a Note or Disclosure doesn't count. A series' later
    /// occurrences are only scheduled, not evidence anyone met: the notes posted for them are.
    /// </summary>
    public static Instant? LastActivityAt(this WorkgroupInfo w, Instant now)
    {
        var lastUpdate = w.LogEntries
            .Where(e => e.Kind is WorkgroupLogKind.Update or WorkgroupLogKind.MeetingNotes)
            .Select(e => (Instant?)e.CreatedAt)
            .Max();
        var lastMeeting = w.Meetings.Where(m => m.StartUtc <= now).Select(m => (Instant?)m.StartUtc).Max();

        return (lastUpdate, lastMeeting) switch
        {
            (null, null) => null,
            (null, var m) => m,
            (var u, null) => u,
            var (u, m) => Instant.Max(u.Value, m.Value)
        };
    }

    /// <summary>Measured from registration when the group has never shown activity.</summary>
    public static Duration? SilenceFor(this WorkgroupInfo w, Instant now)
    {
        var since = w.LastActivityAt(now) ?? w.RegisteredAt;
        return since is null ? null : now - since.Value;
    }

    public static bool IsUpdateDue(this WorkgroupInfo w, Instant now) =>
        w.Status == WorkgroupStatus.Active
        && w.SilenceFor(now) is { } silence
        && silence >= UpdateDueAfter;

    public static bool IsRegistrationOverdue(this WorkgroupInfo w, Instant now) =>
        w.Status is WorkgroupStatus.Applied or WorkgroupStatus.Referred
        && now - w.AppliedAt >= RegistrationDueAfter;

    public static bool IsOpenForComment(this WorkgroupDocumentInfo d, Instant now) =>
        d.Status == WorkgroupDocumentStatus.Published
        && d.CommentsOpenAt is { } from
        && d.CommentsCloseAt is { } to
        && now >= from
        && now < to;

    public static bool IsOpenForComment(this WorkgroupInfo w, Instant now) =>
        w.Documents.Any(d => d.IsOpenForComment(now));

    public static IEnumerable<WorkgroupDocumentInfo> AwaitingDisposition(this WorkgroupInfo w) =>
        w.Documents.Where(d => d.Status == WorkgroupDocumentStatus.Delivered
            && d.Disposition is null or WorkgroupDisposition.Deferred);

    public static bool HasOverdueDisposition(this WorkgroupInfo w, Instant now) =>
        w.AwaitingDisposition().Any(d => d.DeliveredAt is { } at && now - at >= DispositionOverdueAfter);
}
