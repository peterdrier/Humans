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

    /// <summary>Dormancy inquiry after this long without an Update entry or a meeting.</summary>
    public static readonly Duration DormancyInquiryAfter = Duration.FromDays(60);

    /// <summary>How long a flagged group stays silent before the Board sees it as a close candidate.</summary>
    public static readonly Duration CloseCandidateAfter = Duration.FromDays(14);

    /// <summary>A status request the group has not answered in this long is overdue.</summary>
    public static readonly Duration StatusRequestOverdueAfter = Duration.FromDays(7);

    /// <summary>One status request per group per this long, per person.</summary>
    public static readonly Duration StatusRequestCooldown = Duration.FromDays(7);

    /// <summary>The Secretary has this long to act on an application (clause 1's fourteen days).</summary>
    public static readonly Duration RegistrationDueAfter = Duration.FromDays(14);

    /// <summary>A delivered document with no disposition after this long is highlighted for the Board.</summary>
    public static readonly Duration DispositionOverdueAfter = Duration.FromDays(60);

    /// <summary>Clause 5: an annual report is due a year after registration, then yearly.</summary>
    public static readonly Duration AnnualReportPeriod = Duration.FromDays(365);

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
    public static Instant? LastActivityAt(this WorkgroupInfo w)
    {
        var lastUpdate = w.LogEntries
            .Where(e => e.Kind == WorkgroupLogKind.Update)
            .Select(e => (Instant?)e.CreatedAt)
            .Max();
        var lastMeeting = w.Meetings.Select(m => (Instant?)m.StartUtc).Max();

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
        var since = w.LastActivityAt() ?? w.RegisteredAt;
        return since is null ? null : now - since.Value;
    }

    public static bool IsUpdateDue(this WorkgroupInfo w, Instant now) =>
        w.Status == WorkgroupStatus.Active
        && w.SilenceFor(now) is { } silence
        && silence >= UpdateDueAfter;

    /// <summary>Sixty days silent and not yet flagged: the job sets DormantSince and asks.</summary>
    public static bool NeedsDormancyInquiry(this WorkgroupInfo w, Instant now) =>
        w.Status == WorkgroupStatus.Active
        && w.DormantSince is null
        && w.SilenceFor(now) is { } silence
        && silence >= DormancyInquiryAfter;

    /// <summary>Flagged, still silent, and past the grace period: the Board is asked to close it.</summary>
    public static bool IsCloseCandidate(this WorkgroupInfo w, Instant now) =>
        w.Status == WorkgroupStatus.Active
        && w.DormantSince is { } since
        && now - since >= CloseCandidateAfter
        && (w.LastActivityAt() is null || w.LastActivityAt() < since);

    /// <summary>The most recent status request nobody has answered with a later Update.</summary>
    public static Instant? UnansweredStatusRequestAt(this WorkgroupInfo w)
    {
        var lastRequest = w.LogEntries
            .Where(e => e.Kind == WorkgroupLogKind.StatusRequested)
            .Select(e => (Instant?)e.CreatedAt)
            .Max();
        if (lastRequest is null)
            return null;

        var answered = w.LogEntries.Any(e => e.Kind == WorkgroupLogKind.Update && e.CreatedAt > lastRequest);
        return answered ? null : lastRequest;
    }

    public static bool IsStatusOverdue(this WorkgroupInfo w, Instant now) =>
        w.UnansweredStatusRequestAt() is { } requested
        && now - requested >= StatusRequestOverdueAfter;

    /// <summary>True when this person asked for a status update too recently to ask again.</summary>
    public static bool StatusRequestOnCooldownFor(this WorkgroupInfo w, Guid userId, Instant now) =>
        w.LogEntries.Any(e => e.Kind == WorkgroupLogKind.StatusRequested
            && e.AuthorUserId == userId
            && now - e.CreatedAt < StatusRequestCooldown);

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

    /// <summary>
    /// Clause 5: an Active group registered more than a year ago with no annual report
    /// published in the last twelve months.
    /// </summary>
    public static bool IsAnnualReportDue(this WorkgroupInfo w, Instant now) =>
        w.Status == WorkgroupStatus.Active
        && w.RegisteredAt is { } registered
        && now - registered >= AnnualReportPeriod
        && !w.Documents.Any(d => d.Kind == WorkgroupDocumentKind.AnnualReport
            && d.Status != WorkgroupDocumentStatus.Draft
            && now - d.UpdatedAt < AnnualReportPeriod);
}
