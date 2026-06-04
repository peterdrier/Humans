using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using NodaTime;

namespace Humans.Application.Interfaces.Surveys;

/// <summary>
/// Survey section service: authoring (create/update/open/close), and — added in later phases —
/// send, submit, results, export and GDPR contribution. Implements <see cref="ISurveyServiceRead"/>
/// (cross-section boundary) and the <see cref="IApplicationService"/> marker. Read methods return
/// DTOs, never EF entities.
/// </summary>
public interface ISurveyService : ISurveyServiceRead, IApplicationService
{
    // ── Authoring ──────────────────────────────────────────────────────────
    /// <summary>All surveys for the admin index (newest first), with invited/response counts.</summary>
    Task<IReadOnlyList<SurveySummary>> GetSummariesAsync(CancellationToken ct = default);

    /// <summary>Loads a survey's full editable graph for the builder, or null if not found.</summary>
    Task<SurveyDetail?> GetForEditAsync(Guid surveyId, CancellationToken ct = default);

    /// <summary>Creates a Draft survey from the builder input; returns the new survey id.</summary>
    Task<Guid> CreateAsync(SurveyEditInput input, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Replaces a survey's editable graph (questions/options reconciled by id). Validates branching.</summary>
    Task UpdateAsync(Guid surveyId, SurveyEditInput input, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Transitions Draft → Open.</summary>
    Task OpenAsync(Guid surveyId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Transitions Open → Closed.</summary>
    Task CloseAsync(Guid surveyId, Guid actorUserId, CancellationToken ct = default);

    // ── Invitations ────────────────────────────────────────────────────────
    /// <summary>Resolves the survey's audience and returns its size; 0 if the survey has no audience.</summary>
    Task<int> PreviewAudienceCountAsync(Guid surveyId, CancellationToken ct = default);

    /// <summary>
    /// Sends the invitation wave: resolves the audience, creates invitations for net-new recipients
    /// (idempotent — already-invited users are skipped, sends never revoke), and queues each email.
    /// Requires the survey to be Open with an audience.
    /// </summary>
    Task<SendResult> SendInvitesAsync(Guid surveyId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Per-invite delivery/participation status for the admin Send page, with display names stitched in. Unsorted — caller sorts.</summary>
    Task<IReadOnlyList<SurveyInviteStatus>> GetInviteStatusesAsync(Guid surveyId, CancellationToken ct = default);
}

// ── Authoring DTOs (co-located) ─────────────────────────────────────────────

/// <summary>Admin-index row: title resolved in the survey's default culture, plus participation counts.</summary>
public sealed record SurveySummary(Guid Id, string Title, SurveyStatus Status, int InvitedCount, int ResponseCount);

/// <summary>A survey loaded for editing: identity + status + the editable graph.</summary>
public sealed record SurveyDetail(Guid Id, SurveyStatus Status, SurveyEditInput Editable);

/// <summary>Everything the builder edits. Question/option <c>Id</c> null = new (assigned on save).</summary>
public sealed record SurveyEditInput(
    LocalizedText Title,
    LocalizedText Intro,
    LocalizedText ThankYou,
    string DefaultCulture,
    bool AllowAnonymous,
    Instant? OpensAt,
    Instant? ClosesAt,
    SurveyAudienceType? AudienceType,
    Guid? AudienceTeamId,
    string? PublicSlug,
    IReadOnlyList<QuestionInput> Questions);

/// <summary>One question in the builder graph.</summary>
public sealed record QuestionInput(
    Guid? Id,
    int PageNumber,
    int Order,
    SurveyQuestionType Type,
    LocalizedText Prompt,
    LocalizedText HelpText,
    bool IsRequired,
    int? RatingMin,
    int? RatingMax,
    LocalizedText RatingMinLabel,
    LocalizedText RatingMaxLabel,
    BranchCondition? ShowIf,
    IReadOnlyList<OptionInput> Options);

/// <summary>One choice option in the builder graph. <c>Value</c> is the stable machine key.</summary>
public sealed record OptionInput(
    Guid? Id,
    int Order,
    string Value,
    LocalizedText Label);

/// <summary>Outcome of a send wave: net-new invitations created, emails queued, and enqueue failures.</summary>
public sealed record SendResult(int InvitationsCreated, int EmailsQueued, int Failed);

/// <summary>One invitee's row on the admin Send page: display name + latest email status + funnel flags.</summary>
public sealed record SurveyInviteStatus(
    Guid UserId,
    string Name,
    EmailOutboxStatus? EmailStatus,
    bool Completed,
    bool Started,
    Instant? SentAt,
    Instant? ReminderSentAt);
