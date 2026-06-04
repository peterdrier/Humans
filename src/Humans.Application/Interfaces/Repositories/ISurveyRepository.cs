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

    /// <summary>Sets an invitation's <c>LatestEmailStatus</c>. No-op if the invitation does not exist.</summary>
    Task UpdateInvitationStatusAsync(Guid id, EmailOutboxStatus status, Instant at, CancellationToken ct = default);

    // ── Answering (wizard entry) ────────────────────────────────────────────
    /// <summary>A single invitation by id, or null if it does not exist. Read-only.</summary>
    Task<SurveyInvitation?> GetInvitationByIdAsync(Guid invitationId, CancellationToken ct = default);

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
}
