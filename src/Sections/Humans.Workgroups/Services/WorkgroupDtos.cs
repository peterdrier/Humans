using Humans.Workgroups.Domain;
using NodaTime;

namespace Humans.Workgroups.Services;

/// <summary>
/// Time-derived facts (update due, overdue, open for comment) are not baked in: <see cref="WorkgroupRhythm"/>
/// computes them against the caller's clock, so a cached snapshot never carries a stale badge.
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
    Instant UpdatedAt);

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
    IReadOnlyList<WorkgroupCommentInfo> Comments);

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
    string? Minutes);

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
