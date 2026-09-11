using Humans.Workgroups.Domain;
using NodaTime;

namespace Humans.Workgroups.Services;

/// <summary>
/// One register entry as it leaves the section: the group and every child collection,
/// projected off the EF entities so no entity crosses the boundary. Time-derived
/// facts (update due, status overdue, open for comment) are NOT baked in — they are
/// computed from these fields by <see cref="WorkgroupRhythm"/> against the caller's
/// clock, so a cached snapshot never carries a stale badge.
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
    Instant? DormantSince,
    IReadOnlyList<WorkgroupMemberInfo> Members,
    IReadOnlyList<WorkgroupMeetingInfo> Meetings,
    IReadOnlyList<WorkgroupLogEntryInfo> LogEntries,
    IReadOnlyList<WorkgroupDocumentInfo> Documents);

/// <param name="LeftAt">Null while the person is a current member.</param>
internal sealed record WorkgroupMemberInfo(
    Guid Id,
    Guid UserId,
    WorkgroupMemberRole Role,
    Instant JoinedAt,
    Instant? LeftAt);

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

/// <summary>
/// The clause 1 application: exactly what the resolution asks for. The applicant is
/// always a coordinator; <paramref name="SecondCoordinatorUserId"/> is the optional
/// second name (one or two coordinators at all times).
/// </summary>
internal sealed record WorkgroupApplication(
    string Name,
    string Purpose,
    string Deliverable,
    WorkgroupDeliverableKind DeliverableKind,
    WorkgroupAudience Audience,
    LocalDate? TargetDate,
    string? DiscordChannelUrl,
    Guid? SecondCoordinatorUserId);

/// <summary>The register fields any member may edit. Changing the deliverable sentence writes ScopeChanged.</summary>
internal sealed record WorkgroupRegisterEdit(
    string Name,
    string Purpose,
    string Deliverable,
    WorkgroupDeliverableKind DeliverableKind,
    WorkgroupAudience Audience,
    LocalDate? TargetDate,
    string? DiscordChannelUrl);

internal sealed record WorkgroupMeetingSave(
    string Title,
    Instant StartUtc,
    Instant EndUtc,
    string? Location,
    string? LocationUrl,
    bool IsPublic,
    string? Minutes);

/// <param name="Kind">A member kind only — Update, Disclosure or Note. System kinds are the section's to write.</param>
internal sealed record WorkgroupLogEntrySave(
    WorkgroupLogKind Kind,
    LocalDate OccurredOn,
    string? Title,
    string? Body);

internal sealed record WorkgroupDocumentSave(
    string Title,
    WorkgroupDocumentKind Kind,
    string Body);

/// <summary>The comment period: a window plus the categories a commenter must pick from.</summary>
internal sealed record WorkgroupCommentWindow(
    Instant OpensAt,
    Instant ClosesAt,
    IReadOnlyList<string> Categories);

/// <summary>
/// Bootstrapping an existing group (design §21): the Secretary applies on behalf, the
/// group goes straight to Active, and <paramref name="RegisteredAt"/> is backdated to
/// when it really started.
/// </summary>
internal sealed record WorkgroupBootstrap(
    WorkgroupApplication Application,
    Guid CoordinatorUserId,
    Instant RegisteredAt);
