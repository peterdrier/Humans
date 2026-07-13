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

    /// <summary>
    /// Machine-translates the survey's authored content (title, intro, thank-you, prompts, help,
    /// rating/option labels) from its default culture into every <paramref name="targetCultures"/>
    /// entry that is still blank — existing text is never overwritten (spec §6.1: pre-fill, then the
    /// author reviews). Returns the number of fields filled; 0 means nothing was missing.
    /// </summary>
    Task<int> PreFillTranslationsAsync(
        Guid surveyId, IReadOnlyList<string> targetCultures, Guid actorUserId, CancellationToken ct = default);

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
    /// dropping answers to questions hidden under branching. Validates the survey is Open and within
    /// its answer window. Individual submissions are never audit-logged (privacy).
    /// </summary>
    Task SubmitResponseAsync(SurveySubmission submission, CancellationToken ct = default);

    /// <summary>
    /// Advances the answering wizard one step from a posted page: captures the page's visible answers
    /// into <paramref name="state"/>, fires the path-specific first-advance funnel side effect,
    /// autosaves Identified drafts, validates required-visible questions, then navigates
    /// back/next — or submits when no visible page remains. Mutates <paramref name="state"/>
    /// (answers, <c>CurrentPage</c>, <c>Started</c>); the caller persists it per the outcome.
    /// </summary>
    Task<SurveyWizardAdvanceResult> AdvanceWizardAsync(
        SurveyWizardState state, int page, bool back, IReadOnlyList<SurveyAnswerInput> postedAnswers,
        CancellationToken ct = default);

    // ── Results ────────────────────────────────────────────────────────────
    /// <summary>
    /// The admin results read model: participation funnel, per-question aggregates over submitted
    /// responses, and the Identified-only respondent drill-down. Null if the survey does not exist.
    /// CompletionTracked/Anonymous responses feed the aggregates but never the drill-down (no identity
    /// exposure). Prompts/labels are resolved in the survey's default culture.
    /// </summary>
    Task<SurveyResultsView?> GetResultsAsync(Guid surveyId, CancellationToken ct = default);

    /// <summary>
    /// The raw per-response export model backing the admin CSV/JSON downloads (and reused by the
    /// analysis API): the question schema plus one row per submitted response, ordered by
    /// <c>SubmittedAt</c>. Null if the survey does not exist. Identity (<c>UserId</c>/<c>UserName</c>)
    /// is populated ONLY for <see cref="ResponseAnonymity.Identified"/> rows; CompletionTracked and
    /// Anonymous rows still appear (so totals reconcile) but carry no identity. Prompts/labels are
    /// resolved in the survey's default culture.
    /// </summary>
    Task<SurveyResponseExport?> GetResponseExportAsync(Guid surveyId, CancellationToken ct = default);
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
    Instant? AudienceLoggedInSince,
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

/// <summary>
/// Per-session state of the answering wizard. The Web layer JSON-serialises it into the HTTP session
/// and hands it to <see cref="ISurveyService.AdvanceWizardAsync"/>, which owns all flow decisions.
/// Answers are keyed by <c>QuestionId.ToString()</c> (Guid object keys don't round-trip through JSON
/// cleanly; string keys do).
/// </summary>
public sealed class SurveyWizardState
{
    public Guid SurveyId { get; set; }
    public Guid? InvitationId { get; set; }   // the token's invitation — all invited tiers (drives Started/Completed funnel flags)
    public Guid? UserId { get; set; }          // the token's user — all invited tiers; the RESPONSE columns are written only for Identified (see submit)
    public Guid? DraftResponseId { get; set; } // Identified draft only (set by StartIdentifiedDraftAsync)
    public ResponseAnonymity Anonymity { get; set; }
    public SurveyInputMethod InputMethod { get; set; } = SurveyInputMethod.UserSpecificLink;
    public string Culture { get; set; } = "en";
    public int CurrentPage { get; set; }
    public bool Started { get; set; }
    public Dictionary<string, SurveyWizardAnswer> Answers { get; set; } = new(StringComparer.Ordinal); // key = QuestionId.ToString()
}

/// <summary>One captured answer in the wizard session.</summary>
public sealed class SurveyWizardAnswer
{
    public List<string> SelectedOptionValues { get; set; } = [];
    public string? TextValue { get; set; }
    public int? RatingValue { get; set; }
}

/// <summary>Where one wizard advance landed. <c>ValidationFailed</c> carries the missing required question ids.</summary>
public enum SurveyWizardOutcome
{
    /// <summary>The survey no longer exists (treat as an invalid link).</summary>
    NotFound,

    /// <summary>The survey is not Open or is outside its answer window.</summary>
    Closed,

    /// <summary>Required visible questions are unanswered; the state stays on the posted page.</summary>
    ValidationFailed,

    /// <summary>Moved to the previous/next visible page (<c>state.CurrentPage</c> updated).</summary>
    Navigated,

    /// <summary>No visible page remained — the response was submitted.</summary>
    Submitted,
}

/// <summary>Outcome of one wizard advance. <see cref="MissingRequired"/> is empty except on <see cref="SurveyWizardOutcome.ValidationFailed"/>.</summary>
public sealed record SurveyWizardAdvanceResult(SurveyWizardOutcome Outcome, IReadOnlyList<Guid> MissingRequired);

// ── Results DTOs (co-located) ───────────────────────────────────────────────

/// <summary>
/// The admin results read model. <see cref="ResponseRate"/> is <see cref="ResponseCount"/> ÷
/// <see cref="InvitedCount"/> (0 when no one was invited). All prompts/labels are resolved in the
/// survey's default culture.
/// </summary>
public sealed record SurveyResultsView(
    Guid SurveyId,
    string Title,
    SurveyStatus Status,
    int InvitedCount,
    int ResponseCount,
    double ResponseRate,
    SurveyFunnel Funnel,
    IReadOnlyList<QuestionAggregate> Questions,
    IReadOnlyList<RespondentDetail> IdentifiedRespondents);

/// <summary>
/// Participation funnel split by entry path: the user-specific link path (per-invitation
/// <c>Started</c> flag vs submitted link responses) and the public slug path (the survey's
/// <c>PublicStartedCount</c> vs submitted slug responses).
/// </summary>
public sealed record SurveyFunnel(int LinkStarted, int LinkFinished, int SlugStarted, int SlugFinished);

/// <summary>
/// One question's aggregate over submitted responses. The populated collection depends on the
/// question type: <see cref="OptionCounts"/> for choice questions, <see cref="RatingDistribution"/>
/// plus <see cref="RatingAverage"/> for rating questions, <see cref="FreeTextAnswers"/> for text
/// questions. The others are empty/null.
/// </summary>
public sealed record QuestionAggregate(
    Guid QuestionId,
    string Prompt,
    SurveyQuestionType Type,
    IReadOnlyList<OptionCount> OptionCounts,
    IReadOnlyList<RatingBucket> RatingDistribution,
    double? RatingAverage,
    IReadOnlyList<string> FreeTextAnswers);

/// <summary>One choice option's tally. <see cref="Percent"/> is the share of responses to that question (0 when none).</summary>
public sealed record OptionCount(string Value, string Label, int Count, double Percent);

/// <summary>One rating value's tally; empty buckets are included across the question's range.</summary>
public sealed record RatingBucket(int Value, int Count);

/// <summary>One Identified respondent's drill-down row: stitched display name + their answers.</summary>
public sealed record RespondentDetail(Guid UserId, string Name, Instant? SubmittedAt, IReadOnlyList<RespondentAnswer> Answers);

/// <summary>One answer in an Identified respondent's drill-down, with choice labels resolved in the default culture.</summary>
public sealed record RespondentAnswer(Guid QuestionId, string Prompt, IReadOnlyList<string> SelectedLabels, string? TextValue, int? RatingValue);

// ── Export DTOs (co-located; raw per-response, shared by CSV/JSON download and the analysis API) ──

/// <summary>
/// The raw export of a survey's submitted responses: the question schema (ordered by page then order)
/// plus one row per response (ordered by submission time). Prompts/labels are resolved in
/// <see cref="DefaultCulture"/>.
/// </summary>
public sealed record SurveyResponseExport(
    Guid SurveyId,
    string Title,
    string DefaultCulture,
    IReadOnlyList<SurveyExportQuestion> Questions,
    IReadOnlyList<SurveyExportRow> Rows);

/// <summary>One question in the export schema. <see cref="Options"/> is empty for non-choice questions.</summary>
public sealed record SurveyExportQuestion(
    Guid QuestionId,
    string Prompt,
    SurveyQuestionType Type,
    IReadOnlyList<SurveyExportOption> Options);

/// <summary>One choice option in the export schema: the stable machine <see cref="Value"/> + its resolved <see cref="Label"/>.</summary>
public sealed record SurveyExportOption(string Value, string Label);

/// <summary>
/// One exported response. <see cref="UserId"/>/<see cref="UserName"/> are populated only for
/// <see cref="ResponseAnonymity.Identified"/> rows; both are null for CompletionTracked/Anonymous.
/// </summary>
public sealed record SurveyExportRow(
    Guid ResponseId,
    ResponseAnonymity Anonymity,
    SurveyInputMethod InputMethod,
    string Culture,
    Instant? SubmittedAt,
    Guid? UserId,
    string? UserName,
    IReadOnlyList<SurveyExportAnswer> Answers);

/// <summary>One answer in an exported response: choice keys + resolved labels, free text, or a rating.</summary>
public sealed record SurveyExportAnswer(
    Guid QuestionId,
    IReadOnlyList<string> SelectedValues,
    IReadOnlyList<string> SelectedLabels,
    string? TextValue,
    int? RatingValue);
