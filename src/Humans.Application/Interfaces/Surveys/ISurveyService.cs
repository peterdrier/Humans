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

    /// <summary>
    /// Job-driven sweep: sends the one-time 7-day reminder to every invitee of an Open survey who
    /// hasn't completed and hasn't already been reminded (<c>SentAt</c> ≥ 7 days ago). Stamps
    /// <c>ReminderSentAt</c> per invitee so it never fires twice (idempotent). Returns the number reminded.
    /// </summary>
    Task<int> SendDueRemindersAsync(CancellationToken ct = default);

    // ── Answering (wizard entry) ────────────────────────────────────────────
    /// <summary>
    /// Resolves a tokenised invite link into the answering context (survey definition + any resumable
    /// Identified draft), or null when the token is invalid/expired or the invitation/survey is gone.
    /// </summary>
    Task<SurveyAnswerContext?> ResolveAnswerContextAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// Creates (or, idempotently, returns the existing) Identified in-progress draft response for the
    /// invitee. Identified is the only resumable tier. Returns the draft response id.
    /// </summary>
    Task<Guid> StartIdentifiedDraftAsync(Guid surveyId, Guid invitationId, Guid userId, string culture, CancellationToken ct = default);

    /// <summary>Marks the invitation's funnel <c>Started</c> flag (set on the first advance past the intro). No-op if the invitation is gone.</summary>
    Task MarkInvitationStartedAsync(Guid invitationId, CancellationToken ct = default);

    /// <summary>
    /// Resolves a public slug into the anonymous answering context (survey id + reused definition), or
    /// null when no survey owns that slug or the slug is blank. The slug is normalised before lookup.
    /// </summary>
    Task<SurveyPublicContext?> ResolvePublicContextAsync(string slug, CancellationToken ct = default);

    /// <summary>Increments the survey's public-path <c>Started</c> funnel counter (slug path has no per-person anchor). No-op if the survey is gone.</summary>
    Task IncrementPublicStartedAsync(Guid surveyId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the answers on an in-progress Identified draft (per-page autosave). The draft's
    /// <c>SubmittedAt</c> stays null. Branching is not re-applied here — final submit is authoritative.
    /// </summary>
    Task SaveDraftAnswersAsync(Guid draftResponseId, IReadOnlyList<SurveyAnswerInput> answers, CancellationToken ct = default);

    /// <summary>
    /// Finalises a wizard submission per its anonymity tier (see <see cref="SurveySubmission"/>),
    /// dropping answers to questions hidden under branching. Individual submissions are never
    /// audit-logged (privacy).
    /// </summary>
    Task SubmitResponseAsync(SurveySubmission submission, CancellationToken ct = default);
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

// ── Answering DTOs (co-located) ─────────────────────────────────────────────

/// <summary>
/// Everything the wizard entry needs for one invited person: the survey definition (reused
/// <see cref="SurveyDetail"/>), the invitee identity from the token's invitation, and any resumable
/// Identified draft. <see cref="HasResumableDraft"/> is true only when an in-progress Identified
/// response already exists for this invitee.
/// </summary>
public sealed record SurveyAnswerContext(
    Guid SurveyId,
    Guid InvitationId,
    Guid UserId,
    SurveyDetail Definition,
    IReadOnlyList<SurveyDraftAnswer> DraftAnswers,
    bool HasResumableDraft);

/// <summary>
/// A survey resolved from its public slug for the anonymous answering path: the survey id plus the
/// reused editable definition (<see cref="SurveyDetail"/>). No invitation/identity — the slug path is
/// always <see cref="ResponseAnonymity.Anonymous"/>.
/// </summary>
public sealed record SurveyPublicContext(Guid SurveyId, SurveyDetail Definition);

/// <summary>One saved answer from a resumable draft, keyed by question id.</summary>
public sealed record SurveyDraftAnswer(
    Guid QuestionId,
    IReadOnlyList<string> SelectedOptionValues,
    string? TextValue,
    int? RatingValue);

/// <summary>
/// A finalised wizard submission. Identity columns (<c>UserId</c>/<c>InvitationId</c>) are written on
/// the response ONLY for <see cref="ResponseAnonymity.Identified"/>; CompletionTracked still flips the
/// invitation's <c>Completed</c> flag (via <c>InvitationId</c>) but stores no link on the response;
/// Anonymous leaves the invitation untouched. <c>DraftResponseId</c> is set only when resuming an
/// Identified draft. <see cref="InputMethod"/> lets the public-slug path (Task 4.4) reuse submit.
/// </summary>
public sealed record SurveySubmission(
    Guid SurveyId,
    Guid? InvitationId,
    Guid? UserId,
    Guid? DraftResponseId,
    ResponseAnonymity Anonymity,
    SurveyInputMethod InputMethod,
    string Culture,
    IReadOnlyList<SurveyAnswerInput> Answers);

/// <summary>One answer in a submission (or a draft autosave), keyed by question id.</summary>
public sealed record SurveyAnswerInput(
    Guid QuestionId,
    IReadOnlyList<string> SelectedOptionValues,
    string? TextValue,
    int? RatingValue);
