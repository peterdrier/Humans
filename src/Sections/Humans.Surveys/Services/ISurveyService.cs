using Humans.Surveys.Contracts;
using Humans.Base.Interfaces;
using Humans.Base.Enums;
using Humans.Surveys.Domain;
using NodaTime;

namespace Humans.Surveys.Services;

/// <summary>
/// Survey section service: authoring (create/update/open/close), and — added in later phases —
/// send, submit, results, export and GDPR contribution. Implements the
/// <see cref="IApplicationService"/> marker. Read methods return DTOs, never EF entities.
/// </summary>
/// <remarks>
/// Two consumers live outside the section: the reminder job in Base, which sees
/// <see cref="Contracts.ISurveyReminderSender"/>, and the Backdoor machine API, which sees
/// <see cref="ISurveyAnalysisRead"/> (nobodies-collective/Humans#1128). Everything else here
/// — authoring, sending, the wizard, submission — has no caller outside Surveys.
/// </remarks>
internal interface ISurveyService : IApplicationService, ISurveyAnalysisRead
{
    // ── Authoring ──────────────────────────────────────────────────────────
    /// <summary>Loads a survey's full editable graph for the builder, or null if not found.</summary>
    Task<SurveyDetail?> GetForEditAsync(Guid surveyId, CancellationToken ct = default);

    /// <summary>Whether any draft or submitted answer has frozen counting-affecting ranked settings.</summary>
    Task<bool> HasSavedAnswersAsync(Guid surveyId, CancellationToken ct = default);

    /// <summary>Creates a Draft survey from the builder input; returns the new survey id.</summary>
    Task<Guid> CreateAsync(SurveyEditInput input, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Replaces a survey's editable graph (questions/options reconciled by id). Validates branching.</summary>
    Task UpdateAsync(Guid surveyId, SurveyEditInput input, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Machine-translates the survey's authored content (title, intro, thank-you, invitation copy,
    /// prompts, help, rating/option/Grid-row labels) from its default culture into every <paramref name="targetCultures"/>
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
    /// <summary>
    /// Resolves the survey's audience and returns the number of net-new recipients who would receive
    /// an invitation on the next send; 0 if there is no configured audience or everyone is invited.
    /// </summary>
    Task<int> PreviewAudienceCountAsync(Guid surveyId, CancellationToken ct = default);

    /// <summary>
    /// Sends the invitation wave: resolves the audience, creates invitations for net-new recipients
    /// (idempotent — already-invited users are skipped, sends never revoke), and queues each email.
    /// Requires the survey to be Open with an audience.
    /// </summary>
    Task<SendResult> SendInvitesAsync(Guid surveyId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Per-invite delivery/participation status for the admin Send page, with display names stitched in. Unsorted — caller sorts.</summary>
    Task<IReadOnlyList<SurveyInviteStatus>> GetInviteStatusesAsync(Guid surveyId, CancellationToken ct = default);

    /// <summary>The current Human's official entry link for an Open survey: their unspent invitation, or the public slug fallback.</summary>
    Task<SurveyOfficialLink?> GetOfficialLinkAsync(
        Guid surveyId,
        Guid userId,
        CancellationToken ct = default);

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

    /// <summary>Whether the Human currently holds active, approved Asociado voting rights.</summary>
    Task<bool> IsEligibleAsociadoAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Creates (or, idempotently, returns the existing) Identified in-progress draft response for the
    /// Human. Identified is the only resumable tier. The participation id may name an emailed invitation
    /// or an unsent public-link ledger row. Returns the draft response id.
    /// </summary>
    Task<Guid> StartIdentifiedDraftAsync(
        Guid surveyId,
        Guid participationId,
        Guid userId,
        SurveyInputMethod inputMethod,
        string culture,
        CancellationToken ct = default);

    /// <summary>Marks the invitation's funnel <c>Started</c> flag (set on the first advance past the intro). No-op if the invitation is gone.</summary>
    Task MarkInvitationStartedAsync(Guid invitationId, CancellationToken ct = default);

    /// <summary>
    /// Resolves a shareable slug into the answering context (survey id + reused definition) and the
    /// current Human's access outcome, or null when no survey owns that slug or the slug is blank.
    /// Anonymous-enabled surveys allow everyone; identified surveys require a logged-in Human who
    /// currently belongs to the configured audience (or any logged-in Human when no audience is set).
    /// The slug is normalised before lookup.
    /// </summary>
    Task<SurveyPublicContext?> ResolvePublicContextAsync(
        string slug,
        Guid? userId,
        CancellationToken ct = default);

    /// <summary>
    /// Gets or creates the logged-in Human's per-survey participation ledger for a public-link
    /// Identified/CompletionTracked start. Returns null when that participation is already complete.
    /// Identified additionally creates/resumes its draft.
    /// </summary>
    Task<SurveyPublicStart?> StartPublicTrackedResponseAsync(
        Guid surveyId,
        Guid userId,
        ResponseAnonymity anonymity,
        string culture,
        CancellationToken ct = default);

    /// <summary>Increments the survey's public-path <c>Started</c> funnel counter (slug path has no per-person anchor). No-op if the survey is gone.</summary>
    Task IncrementPublicStartedAsync(Guid surveyId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the answers on an in-progress Identified draft (per-page autosave), together with the
    /// current entry path and culture. The draft's <c>SubmittedAt</c> stays null. Branching is not
    /// re-applied here — final submit is authoritative.
    /// </summary>
    Task SaveDraftAnswersAsync(
        Guid draftResponseId,
        IReadOnlyList<SurveyAnswerInput> answers,
        SurveyInputMethod inputMethod,
        string culture,
        CancellationToken ct = default);

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
    Task<SurveyScopedResults?> GetScopedResultsAsync(
        Guid surveyId,
        SurveyResultsScope scope,
        CancellationToken ct = default);

    Task SetRankedAvailabilityAsync(
        Guid surveyId,
        Guid questionId,
        IReadOnlyList<string> unavailableValues,
        Guid actorUserId,
        CancellationToken ct = default);

}

internal sealed record SurveyOfficialLink(string? InvitationToken, string? PublicSlug);

internal sealed record SurveyScopedResults(
    SurveyResultsView Results,
    int SelectedResponseCount,
    SurveyResultsScope Scope,
    bool IsEmbargoed = false,
    IReadOnlyDictionary<Guid, RankedQuestionResult>? RankedQuestions = null);

internal sealed record RankedQuestionResult(
    IReadOnlyList<RankedCandidateResult> Candidates,
    RankedMethodResult OriginalOfficialResult,
    RankedMethodResult CurrentOfficialResult,
    IReadOnlyList<RankedMethodResult> Methods,
    IReadOnlyList<PairwiseContest> Pairwise,
    IReadOnlyList<string> OriginalPreferenceCycle,
    IReadOnlyList<string> CurrentPreferenceCycle,
    IReadOnlyList<string> UnavailableValues);

internal sealed record RankedCandidateResult(
    string Value,
    string Label,
    bool IsAvailable,
    int RejectionCount,
    double RejectionPercent);
internal sealed record RankedMethodResult(string Method, string? WinnerValue, string? WinnerLabel, bool TieBreakUsed);

internal enum SurveyResultsScope
{
    Combined,
    Unique,
    Anonymous,
}

// ── Authoring DTOs (co-located) ─────────────────────────────────────────────

/// <summary>A survey loaded for editing: identity + status + the editable graph.</summary>
internal sealed record SurveyDetail(Guid Id, SurveyStatus Status, SurveyEditInput Editable);

/// <summary>Everything the builder edits. Question/option <c>Id</c> null = new (assigned on save).</summary>
internal sealed record SurveyEditInput(
    LocalizedText Title,
    LocalizedText Intro,
    LocalizedText ThankYou,
    LocalizedText InvitationEmailSubject,
    LocalizedText InvitationEmailMessage,
    string DefaultCulture,
    bool AllowAnonymous,
    Instant? OpensAt,
    Instant? ClosesAt,
    SurveyAudienceType? AudienceType,
    Guid? AudienceTeamId,
    Instant? AudienceLoggedInSince,
    string? PublicSlug,
    IReadOnlyList<QuestionInput> Questions,
    bool IsAsociadoVote = false);

/// <summary>One question in the builder graph.</summary>
internal sealed record QuestionInput(
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
    IReadOnlyList<OptionInput> Options,
    GridSelectionMode? GridSelectionMode = null,
    IReadOnlyList<GridRowInput>? GridRows = null,
    IReadOnlyList<InformationImageInput>? InformationImages = null,
    RankedQuestionSettings? RankedSettings = null,
    IReadOnlyList<string>? RankedUnavailableOptionValues = null);

/// <summary>One choice option in the builder graph. <c>Value</c> is the stable machine key.</summary>
internal sealed record OptionInput(
    Guid? Id,
    int Order,
    string Value,
    LocalizedText Label);

/// <summary>One Grid row in the builder graph. <c>Value</c> is the stable machine key.</summary>
internal sealed record GridRowInput(string Value, LocalizedText Label);

/// <summary>
/// One Information-item image. Existing persisted metadata is populated on reads; a new upload is
/// populated only for the duration of an authoring save.
/// </summary>
internal sealed record InformationImageInput(
    Guid? Id,
    LocalizedText Label,
    LocalizedText AltText,
    string? StoragePath = null,
    string? ContentType = null,
    string? FileName = null,
    SurveyImageUpload? Upload = null);

internal sealed record SurveyImageUpload(Stream Content, string ContentType, string FileName, long Length);

/// <summary>Outcome of a send wave: net-new invitations created, emails queued, and enqueue failures.</summary>
internal sealed record SendResult(int InvitationsCreated, int EmailsQueued, int Failed);

/// <summary>One invitee's row on the admin Send page: display name + latest email status + funnel flags.</summary>
internal sealed record SurveyInviteStatus(
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
internal sealed record SurveyAnswerContext(
    Guid SurveyId,
    Guid InvitationId,
    Guid UserId,
    SurveyDetail Definition,
    IReadOnlyList<SurveyDraftAnswer> DraftAnswers,
    bool HasResumableDraft,
    bool IsEligible = true);

/// <summary>
/// A survey resolved from its public slug: the survey id plus the reused editable definition
/// (<see cref="SurveyDetail"/>). Representation is selected when the respondent starts.
/// </summary>
internal sealed record SurveyPublicContext(
    Guid SurveyId,
    SurveyDetail Definition,
    SurveyPublicAccess Access = SurveyPublicAccess.Allowed);

internal enum SurveyPublicAccess
{
    Allowed,
    AuthenticationRequired,
    Ineligible,
}

/// <summary>
/// The logged-in public-link start result. <c>ParticipationId</c> is the existing or newly-created
/// survey/user ledger row; <c>DraftResponseId</c> is present only for Identified.
/// <c>DraftAnswers</c> restores an existing Identified public draft into the wizard session.
/// </summary>
internal sealed record SurveyPublicStart(
    Guid ParticipationId,
    Guid? DraftResponseId,
    IReadOnlyList<SurveyDraftAnswer> DraftAnswers);

/// <summary>One saved answer from a resumable draft, keyed by question id.</summary>
internal sealed record SurveyDraftAnswer(
    Guid QuestionId,
    IReadOnlyList<string> SelectedOptionValues,
    string? TextValue,
    int? RatingValue,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? GridSelections = null,
    RankedAnswer? RankedValue = null);

/// <summary>
/// A finalised wizard submission. Identity columns (<c>UserId</c>/<c>InvitationId</c>) are written on
/// the response ONLY for <see cref="ResponseAnonymity.Identified"/>; CompletionTracked still flips the
/// invitation's <c>Completed</c> flag (via <c>InvitationId</c>) but stores no link on the response;
/// Anonymous leaves the invitation untouched. <c>DraftResponseId</c> is set only when resuming an
/// Identified draft. <see cref="InputMethod"/> lets the public-slug path reuse submit.
/// </summary>
internal sealed record SurveySubmission(
    Guid SurveyId,
    Guid? InvitationId,
    Guid? UserId,
    Guid? DraftResponseId,
    ResponseAnonymity Anonymity,
    SurveyInputMethod InputMethod,
    string Culture,
    IReadOnlyList<SurveyAnswerInput> Answers);

/// <summary>One answer in a submission (or a draft autosave), keyed by question id.</summary>
internal sealed record SurveyAnswerInput(
    Guid QuestionId,
    IReadOnlyList<string> SelectedOptionValues,
    string? TextValue,
    int? RatingValue,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? GridSelections = null,
    RankedAnswer? RankedValue = null);

/// <summary>
/// Per-session state of the answering wizard. The Web layer JSON-serialises it into the HTTP session
/// and hands it to <see cref="ISurveyService.AdvanceWizardAsync"/>, which owns all flow decisions.
/// Answers are keyed by <c>QuestionId.ToString()</c> (Guid object keys don't round-trip through JSON
/// cleanly; string keys do).
/// </summary>
internal sealed class SurveyWizardState
{
    public Guid SurveyId { get; set; }
    public Guid? InvitationId { get; set; }   // invite or tracked-public participation row (drives Completed; Started is invite-path only)
    public Guid? UserId { get; set; }          // tracked respondent; the RESPONSE columns are written only for Identified (see submit)
    public Guid? DraftResponseId { get; set; } // Identified draft only (set by StartIdentifiedDraftAsync)
    public ResponseAnonymity Anonymity { get; set; }
    public SurveyInputMethod InputMethod { get; set; } = SurveyInputMethod.UserSpecificLink;
    public string Culture { get; set; } = "en";
    public int CurrentPage { get; set; }
    public bool Started { get; set; }
    public Dictionary<string, SurveyWizardAnswer> Answers { get; set; } = new(StringComparer.Ordinal); // key = QuestionId.ToString()

    /// <summary>
    /// Fully Anonymous public state may continue without a principal. Tracked public state belongs
    /// only to the same currently authenticated Human who created it.
    /// </summary>
    public bool IsPubliclyAccessibleBy(Guid? currentUserId)
        => Anonymity == ResponseAnonymity.Anonymous
           || (UserId is not null && UserId == currentUserId);
}

/// <summary>One captured answer in the wizard session.</summary>
internal sealed class SurveyWizardAnswer
{
    public List<string> SelectedOptionValues { get; set; } = [];
    public Dictionary<string, List<string>> GridSelections { get; set; } = new(StringComparer.Ordinal);
    public RankedAnswer? RankedValue { get; set; }
    public string? TextValue { get; set; }
    public int? RatingValue { get; set; }
}

/// <summary>Where one wizard advance landed. <c>ValidationFailed</c> carries question-level validation details.</summary>
internal enum SurveyWizardOutcome
{
    /// <summary>The survey no longer exists (treat as an invalid link).</summary>
    NotFound,

    /// <summary>The survey is not Open or is outside its answer window.</summary>
    Closed,

    /// <summary>The Human no longer holds active, approved Asociado voting rights.</summary>
    Ineligible,

    /// <summary>One or more visible answers are missing or invalid; the state stays on the relevant page.</summary>
    ValidationFailed,

    /// <summary>Moved to the previous/next visible page (<c>state.CurrentPage</c> updated).</summary>
    Navigated,

    /// <summary>No visible page remained — the response was submitted.</summary>
    Submitted,
}

/// <summary>Outcome of one wizard advance. Validation collections are empty except on <see cref="SurveyWizardOutcome.ValidationFailed"/>.</summary>
internal sealed record SurveyWizardAdvanceResult(
    SurveyWizardOutcome Outcome,
    IReadOnlyList<Guid> MissingRequired,
    IReadOnlyList<Guid>? InvalidAnswers = null);
