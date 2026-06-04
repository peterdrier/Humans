using Humans.Domain.Attributes;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the Survey aggregate: <c>surveys</c>, <c>survey_questions</c>,
/// <c>survey_question_options</c>, <c>survey_invitations</c>, <c>survey_responses</c>,
/// and <c>survey_answers</c>.
/// </summary>
/// <remarks>
/// Reads are <c>AsNoTracking</c>. Mutating methods load tracked entities and save changes
/// atomically inside a single <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{HumansDbContext}"/>-owned
/// context. Cross-domain references (<c>Survey.CreatedByUserId</c>, <c>SurveyInvitation.UserId</c>,
/// <c>Survey.AudienceTeamId</c>) are bare <see cref="System.Guid"/> columns with no navigation — the
/// application service stitches display data via <c>IUserServiceRead</c>/<c>ITeamServiceRead</c>.
/// Declared <c>partial</c>; each phase extends it with that phase's reads/writes.
/// </remarks>
[Section("Surveys")]
public partial interface ISurveyRepository : IRepository
{
    /// <summary>Loads a survey with its questions (ordered) and their options (ordered). Null if not found. Read-only.</summary>
    Task<Survey?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>All surveys (no question graph) for the admin index, newest first. Read-only.</summary>
    Task<IReadOnlyList<Survey>> GetAllSummariesAsync(CancellationToken ct = default);

    /// <summary>Inserts a new survey and its authoring graph.</summary>
    Task AddAsync(Survey survey, CancellationToken ct = default);

    /// <summary>Full-graph upsert: reconciles the survey's questions and options against the persisted graph by id.</summary>
    Task UpdateAsync(Survey survey, CancellationToken ct = default);

    /// <summary>Current status of a survey, or null if it does not exist. Read-only.</summary>
    Task<SurveyStatus?> GetStatusAsync(Guid id, CancellationToken ct = default);

    /// <summary>Sets a survey's status and stamps <c>UpdatedAt</c>. No-op if the survey does not exist.</summary>
    Task SetStatusAsync(Guid id, SurveyStatus status, Instant updatedAt, CancellationToken ct = default);

    /// <summary>Invitation count per survey id (for the admin index). Surveys with no invitations are absent. Read-only.</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetInvitedCountsBySurveyAsync(CancellationToken ct = default);

    /// <summary>Submitted-response count per survey id (for the admin index). Drafts (<c>SubmittedAt is null</c>) excluded. Read-only.</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetResponseCountsBySurveyAsync(CancellationToken ct = default);

    /// <summary>User ids already invited to a survey (the idempotency ledger for the send wave). Read-only.</summary>
    Task<IReadOnlySet<Guid>> GetInvitedUserIdsAsync(Guid surveyId, CancellationToken ct = default);

    /// <summary>All invitations for a survey (for the admin Send status list). No display ordering — caller sorts. Read-only.</summary>
    Task<IReadOnlyList<SurveyInvitation>> GetInvitationsAsync(Guid surveyId, CancellationToken ct = default);

    /// <summary>Inserts a single invitation row and saves (per-invite commit so the send wave is restartable).</summary>
    Task AddInvitationAndSaveAsync(SurveyInvitation invitation, CancellationToken ct = default);

    /// <summary>
    /// Invitations due for the one-time 7-day reminder: their survey is <c>Open</c>, not yet
    /// <c>Completed</c>, no reminder sent yet, and sent on or before <paramref name="cutoff"/>. No
    /// display ordering. Read-only.
    /// </summary>
    Task<IReadOnlyList<SurveyInvitation>> GetInvitationsDueForReminderAsync(Instant cutoff, CancellationToken ct = default);

    /// <summary>Stamps an invitation's <c>ReminderSentAt</c> (the one-shot reminder ledger). No-op if the invitation is gone.</summary>
    Task SetReminderSentAsync(Guid invitationId, Instant at, CancellationToken ct = default);

    /// <summary>Sets an invitation's <c>LatestEmailStatus</c>. No-op if the invitation does not exist.</summary>
    Task UpdateInvitationStatusAsync(Guid id, EmailOutboxStatus status, Instant at, CancellationToken ct = default);

    // ── Answering (wizard entry) ────────────────────────────────────────────
    /// <summary>A single invitation by id, or null if it does not exist. Read-only.</summary>
    Task<SurveyInvitation?> GetInvitationByIdAsync(Guid invitationId, CancellationToken ct = default);

    /// <summary>The id of the survey owning <paramref name="slug"/> (already normalised), or null if none. Read-only.</summary>
    Task<Guid?> GetIdByPublicSlugAsync(string slug, CancellationToken ct = default);

    /// <summary>Increments a survey's <c>PublicStartedCount</c> by one. No-op if the survey does not exist.</summary>
    Task IncrementPublicStartedAsync(Guid surveyId, CancellationToken ct = default);

    /// <summary>The invitee's in-progress Identified draft response (with answers), or null. No display ordering. Read-only.</summary>
    Task<SurveyResponse?> GetDraftResponseAsync(Guid surveyId, Guid userId, CancellationToken ct = default);

    /// <summary>Inserts a response row and saves.</summary>
    Task AddResponseAsync(SurveyResponse response, CancellationToken ct = default);

    // ── Answering (submit) ──────────────────────────────────────────────────
    /// <summary>
    /// Replaces a draft response's answers with <paramref name="answers"/> (load tracked, remove
    /// existing, add new) and saves. When <paramref name="submittedAt"/> is non-null it also stamps
    /// <c>SubmittedAt</c> (the Identified finalise). No-op if the response does not exist.
    /// </summary>
    Task SaveDraftAnswersAsync(Guid draftResponseId, IReadOnlyList<SurveyAnswer> answers, Instant? submittedAt, CancellationToken ct = default);

    /// <summary>Inserts a response together with its answer graph and saves (CompletionTracked/Anonymous final submit).</summary>
    Task AddResponseWithAnswersAndSaveAsync(SurveyResponse response, CancellationToken ct = default);

    /// <summary>Sets an invitation's <c>Completed</c> flag (no timestamp — see entity). No-op if the invitation is gone.</summary>
    Task SetInvitationCompletedAsync(Guid invitationId, CancellationToken ct = default);

    /// <summary>Sets an invitation's <c>Started</c> flag (no timestamp). No-op if the invitation is gone.</summary>
    Task MarkInvitationStartedAsync(Guid invitationId, CancellationToken ct = default);

    // ── Results ────────────────────────────────────────────────────────────
    /// <summary>All submitted responses (<c>SubmittedAt is not null</c>) for a survey, with their answer graph. No display ordering. Read-only.</summary>
    Task<IReadOnlyList<SurveyResponse>> GetResponsesForResultsAsync(Guid surveyId, CancellationToken ct = default);

    /// <summary>Count of a survey's invitations whose funnel <c>Started</c> flag is set. Read-only.</summary>
    Task<int> GetStartedInvitationCountAsync(Guid surveyId, CancellationToken ct = default);

    // ── GDPR export ────────────────────────────────────────────────────────
    /// <summary>
    /// A user's submitted <see cref="ResponseAnonymity.Identified"/> responses (with answer graph) for
    /// the GDPR Article 15 export. CompletionTracked/Anonymous responses carry no <c>UserId</c> and are
    /// excluded — they are not personal data linkable to the user. No display ordering. Read-only.
    /// </summary>
    Task<IReadOnlyList<SurveyResponse>> GetIdentifiedResponsesForUserAsync(Guid userId, CancellationToken ct = default);
}
