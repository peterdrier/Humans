using Humans.Workgroups.Domain;
using NodaTime;

namespace Humans.Workgroups.Services;

/// <summary>
/// The reporting rhythm of design §13, as pure functions over a
/// <see cref="WorkgroupInfo"/> and a clock. Kept out of the projection on purpose: the
/// register snapshot is cached, so anything derived from "now" has to be computed at
/// read time or it would go stale in the cache. The daily job and the views ask the
/// same questions here, so page badge and job action can never disagree.
/// </summary>
internal static class WorkgroupRhythm
{
    /// <summary>Monthly update due after this long without an Update entry or a meeting.</summary>
    public static readonly Duration UpdateDueAfter = Duration.FromDays(30);

    /// <summary>The Secretary has this long to act on an application (clause 1's fourteen days).</summary>
    public static readonly Duration RegistrationDueAfter = Duration.FromDays(14);

    /// <summary>A delivered document with no disposition after this long is highlighted for the Board.</summary>
    public static readonly Duration DispositionOverdueAfter = Duration.FromDays(60);

    /// <summary>Current members — a soft leave keeps the row but drops off the roster.</summary>
    public static IEnumerable<WorkgroupMemberInfo> CurrentMembers(this WorkgroupInfo w) =>
        w.Members.Where(m => m.LeftAt is null);

    /// <summary>The one or two people named on the register.</summary>
    public static IEnumerable<WorkgroupMemberInfo> Coordinators(this WorkgroupInfo w) =>
        w.CurrentMembers().Where(m => m.Role == WorkgroupMemberRole.Coordinator);

    public static IReadOnlyList<Guid> CoordinatorUserIds(this WorkgroupInfo w) =>
        w.Coordinators().Select(m => m.UserId).ToList();

    public static IReadOnlyList<Guid> CurrentMemberUserIds(this WorkgroupInfo w) =>
        w.CurrentMembers().Select(m => m.UserId).ToList();

    public static bool IsMember(this WorkgroupInfo w, Guid userId) =>
        w.CurrentMembers().Any(m => m.UserId == userId);

    /// <summary>Member mutations are frozen once a group ends, and before it is registered.</summary>
    public static bool AcceptsMemberWork(this WorkgroupInfo w) => w.Status == WorkgroupStatus.Active;

    /// <summary>Meetings the group still has: not soft-deleted, and this is the only place that filters them.</summary>
    public static IEnumerable<WorkgroupMeetingInfo> UpcomingMeetings(this WorkgroupInfo w, Instant now) =>
        w.Meetings.Where(m => m.StartUtc >= now).OrderBy(m => m.StartUtc);

    public static WorkgroupMeetingInfo? NextMeeting(this WorkgroupInfo w, Instant now) =>
        w.UpcomingMeetings(now).FirstOrDefault();

    /// <summary>
    /// The last time the group showed a sign of life: an Update entry or a meeting.
    /// A Note or a Disclosure is not activity — §13 counts Update or Meeting only.
    /// </summary>
    public static Instant? LastActivityAt(this WorkgroupInfo w, Instant now)
    {
        var lastUpdate = w.LogEntries
            .Where(e => e.Kind == WorkgroupLogKind.Update)
            .Select(e => (Instant?)e.CreatedAt)
            .Max();
        var lastMeeting = w.Meetings.Where(m => m.StartUtc <= now).Select(m => (Instant?)m.StartUtc).Max();

        return (lastUpdate, lastMeeting) switch
        {
            (null, null) => null,
            (null, var m) => m,
            (var u, null) => u,
            var (u, m) => Instant.Max(u!.Value, m!.Value)
        };
    }

    /// <summary>
    /// Silence measured from the last Update or meeting, or from registration when the
    /// group has never shown one.
    /// </summary>
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

    /// <summary>A document is open for comment when now falls inside its window.</summary>
    public static bool IsOpenForComment(this WorkgroupDocumentInfo d, Instant now) =>
        d.Status == WorkgroupDocumentStatus.Published
        && d.CommentsOpenAt is { } from
        && d.CommentsCloseAt is { } to
        && now >= from
        && now < to;

    public static bool IsOpenForComment(this WorkgroupInfo w, Instant now) =>
        w.Documents.Any(d => d.IsOpenForComment(now));

    /// <summary>Delivered documents the Board still owes a written reply.</summary>
    public static IEnumerable<WorkgroupDocumentInfo> AwaitingDisposition(this WorkgroupInfo w) =>
        w.Documents.Where(d => d.Status == WorkgroupDocumentStatus.Delivered
            && d.Disposition is null or WorkgroupDisposition.Deferred);

    public static bool HasOverdueDisposition(this WorkgroupInfo w, Instant now) =>
        w.AwaitingDisposition().Any(d => d.DeliveredAt is { } at && now - at >= DispositionOverdueAfter);
}
