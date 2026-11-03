using Humans.Workgroups.Domain;
using NodaTime;

namespace Humans.Workgroups.Services;

/// <summary>
/// Time-derived facts are evaluated against the caller's clock, so a cached snapshot never carries a stale badge.
/// </summary>
internal sealed record WorkgroupInfo(
    Guid Id,
    string Slug,
    string Name,
    string Purpose,
    string Deliverable,
    WorkgroupDeliverableKind DeliverableKind,
    WorkgroupAudience Audience,
    LocalDate? TargetDate,
    WorkgroupStatus Status,
    WorkgroupDormantReason? DormantReason,
    string? Reasons,
    string? DriveFolderId,
    string? DiscordChannelUrl,
    Guid? AppliedByUserId,
    Instant AppliedAt,
    Instant? RegisteredAt,
    Instant? EndedAt,
    IReadOnlyList<WorkgroupMemberInfo> Members,
    IReadOnlyList<WorkgroupMeetingInfo> Meetings,
    IReadOnlyList<WorkgroupLogEntryInfo> LogEntries,
    IReadOnlyList<WorkgroupDocumentInfo> Documents,
    decimal? BudgetAmount,
    int? HoldedAccountNumber,
    string? HoldedAccountId,
    bool IsSensitive = false)
{
    public bool HasBudget => BudgetAmount is not null;

    public static readonly Duration UpdateDueAfter = Duration.FromDays(30);

    /// <summary>Clause 1's fourteen days for the Secretary to act on an application.</summary>
    public static readonly Duration RegistrationDueAfter = Duration.FromDays(14);

    /// <summary>A delivered document with no disposition after this long is highlighted for the Board.</summary>
    public static readonly Duration DispositionOverdueAfter = Duration.FromDays(60);

    /// <summary>A soft leave keeps the row off the roster; a pending request isn't on it yet.</summary>
    public IEnumerable<WorkgroupMemberInfo> CurrentMembers() =>
        Members.Where(m => m.LeftAt is null && !m.IsPending);

    public IEnumerable<WorkgroupMemberInfo> PendingMembers() =>
        Members.Where(m => m.LeftAt is null && m.IsPending);

    public bool IsPendingMember(Guid userId) =>
        PendingMembers().Any(m => m.UserId == userId);

    public bool IsCoordinator(Guid userId) =>
        Coordinators().Any(m => m.UserId == userId);

    public IEnumerable<WorkgroupMemberInfo> Coordinators() =>
        CurrentMembers().Where(m => m.Role == WorkgroupMemberRole.Coordinator);

    public IReadOnlyList<Guid> CoordinatorUserIds() =>
        Coordinators().Select(m => m.UserId).ToList();

    public IReadOnlyList<Guid> CurrentMemberUserIds() =>
        CurrentMembers().Select(m => m.UserId).ToList();

    public bool IsMember(Guid userId) =>
        CurrentMembers().Any(m => m.UserId == userId);

    public bool AcceptsMemberWork() => Status == WorkgroupStatus.Active;

    public WorkgroupMeetingInfo? NextMeeting(Instant now) =>
        Meetings.Where(m => m.StartUtc >= now).MinBy(m => m.StartUtc);

    /// <summary>An Update entry or a meeting; a Note or Disclosure doesn't count.</summary>
    public Instant? LastActivityAt(Instant now)
    {
        var lastUpdate = LogEntries
            .Where(e => e.Kind == WorkgroupLogKind.Update)
            .Select(e => (Instant?)e.CreatedAt)
            .Max();
        var lastMeeting = Meetings.Where(m => m.StartUtc <= now).Select(m => (Instant?)m.StartUtc).Max();

        return (lastUpdate, lastMeeting) switch
        {
            (null, null) => null,
            (null, var m) => m,
            (var u, null) => u,
            var (u, m) => Instant.Max(u.Value, m.Value)
        };
    }

    /// <summary>Measured from registration when the group has never shown activity.</summary>
    public Duration? SilenceFor(Instant now)
    {
        var since = LastActivityAt(now) ?? RegisteredAt;
        return since is null ? null : now - since.Value;
    }

    public bool IsUpdateDue(Instant now) =>
        Status == WorkgroupStatus.Active
        && SilenceFor(now) is { } silence
        && silence >= UpdateDueAfter;

    public bool IsRegistrationOverdue(Instant now) =>
        Status is WorkgroupStatus.Applied or WorkgroupStatus.Referred
        && now - AppliedAt >= RegistrationDueAfter;

    public bool IsOpenForComment(Instant now) =>
        Documents.Any(d => d.IsOpenForComment(now));

    public IEnumerable<WorkgroupDocumentInfo> AwaitingDisposition() =>
        Documents.Where(d => d.Status == WorkgroupDocumentStatus.Delivered
            && d.Disposition is null or WorkgroupDisposition.Deferred);

    public bool HasOverdueDisposition(Instant now) =>
        AwaitingDisposition().Any(d => d.DeliveredAt is { } at && now - at >= DispositionOverdueAfter);
}

/// <summary><paramref name="LeftAt"/> is null for a current member; <paramref name="IsPending"/> is true until a
/// coordinator approves the request.</summary>
internal sealed record WorkgroupMemberInfo(
    Guid Id,
    Guid UserId,
    WorkgroupMemberRole Role,
    Instant JoinedAt,
    Instant? LeftAt,
    bool IsPending = false);

internal sealed record WorkgroupMeetingInfo(
    Guid Id,
    string Title,
    Instant StartUtc,
    Instant EndUtc,
    string? Location,
    string? LocationUrl,
    bool IsPublic,
    string? Minutes,
    Guid? CreatedByUserId,
    Instant CreatedAt,
    Instant UpdatedAt,
    string? Description = null);

internal sealed record WorkgroupLogEntryInfo(
    Guid Id,
    WorkgroupLogKind Kind,
    LocalDate OccurredOn,
    string? Title,
    string? Body,
    Guid? AuthorUserId,
    Guid? DocumentId,
    Guid? SurveyId,
    Instant CreatedAt,
    Instant UpdatedAt);

internal sealed record WorkgroupDocumentInfo(
    Guid Id,
    Guid WorkgroupId,
    string Title,
    WorkgroupDocumentKind Kind,
    string Body,
    WorkgroupDocumentStatus Status,
    IReadOnlyList<string> CommentCategories,
    Instant? CommentsOpenAt,
    Instant? CommentsCloseAt,
    Instant? DeliveredAt,
    WorkgroupDisposition? Disposition,
    string? DispositionNote,
    Instant? DispositionAt,
    Guid? DispositionByUserId,
    Guid? CreatedByUserId,
    Guid? UpdatedByUserId,
    Instant CreatedAt,
    Instant UpdatedAt,
    IReadOnlyList<WorkgroupCommentInfo> Comments)
{
    public bool IsOpenForComment(Instant now) =>
        Status == WorkgroupDocumentStatus.Published
        && CommentsOpenAt is { } from
        && CommentsCloseAt is { } to
        && now >= from
        && now < to;
}

internal sealed record WorkgroupCommentInfo(
    Guid Id,
    Guid DocumentId,
    string Category,
    Guid? AuthorUserId,
    string Body,
    Instant CreatedAt,
    WorkgroupCommentDisposition Disposition,
    string? Response,
    Guid? RespondedByUserId,
    Instant? RespondedAt,
    Instant? HiddenAt,
    Guid? HiddenByUserId,
    string? HiddenReason);

/// <summary>The applicant is always a coordinator; <paramref name="SecondCoordinatorUserId"/> is optional.</summary>
internal sealed record WorkgroupApplication(
    string Name,
    string Purpose,
    string Deliverable,
    WorkgroupDeliverableKind DeliverableKind,
    WorkgroupAudience Audience,
    LocalDate? TargetDate,
    string? DiscordChannelUrl,
    Guid? SecondCoordinatorUserId,
    bool IsSensitive = false);

internal sealed record WorkgroupRegisterEdit(
    string Name,
    string Purpose,
    string Deliverable,
    WorkgroupDeliverableKind DeliverableKind,
    WorkgroupAudience Audience,
    LocalDate? TargetDate,
    string? DiscordChannelUrl,
    bool IsSensitive = false);

internal sealed record WorkgroupMeetingSave(
    string Title,
    Instant StartUtc,
    Instant EndUtc,
    string? Location,
    string? LocationUrl,
    bool IsPublic,
    string? Minutes,
    string? Description = null);

/// <summary><paramref name="Kind"/> must be a member kind: Update, Disclosure, StatusRequested or Note.</summary>
internal sealed record WorkgroupLogEntrySave(
    WorkgroupLogKind Kind,
    LocalDate OccurredOn,
    string? Title,
    string? Body);

internal sealed record WorkgroupDocumentSave(
    string Title,
    WorkgroupDocumentKind Kind,
    string Body);

internal sealed record WorkgroupCommentWindow(
    Instant OpensAt,
    Instant ClosesAt,
    IReadOnlyList<string> Categories);

/// <summary>Bootstrapping: goes straight to Active, with <paramref name="RegisteredAt"/> backdated.</summary>
internal sealed record WorkgroupBootstrap(
    WorkgroupApplication Application,
    Guid CoordinatorUserId,
    Instant RegisteredAt,
    WorkgroupBudgetSave? Budget = null);

/// <summary><paramref name="Amount"/> null clears the budget but keeps the binding. <paramref name="ExistingAccountNum"/>
/// links that Holded account; null creates "Workgroups / {Name}" when nothing is bound, else changes the amount only.</summary>
internal sealed record WorkgroupBudgetSave(decimal? Amount, int? ExistingAccountNum);
