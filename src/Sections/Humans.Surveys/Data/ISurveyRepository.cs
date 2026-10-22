using Humans.Surveys.Contracts;
using Humans.Base.Interfaces.Repositories;
using Humans.Surveys.Domain;
using Humans.Base.Enums;
using NodaTime;

namespace Humans.Surveys.Data;

/// <summary>
/// Reads are <c>AsNoTracking</c>. Mutating methods load tracked entities and save atomically in one context.
/// </summary>
internal interface ISurveyRepository : IRepository
{
    /// <summary>Questions and options ordered. Null if not found.</summary>
    Task<Survey?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>No question graph; newest first.</summary>
    Task<IReadOnlyList<Survey>> GetAllSummariesAsync(CancellationToken ct = default);

    Task AddAsync(Survey survey, CancellationToken ct = default);

    /// <summary>Reconciles questions and options against the persisted graph by id.</summary>
    Task UpdateAsync(Survey survey, CancellationToken ct = default);

    /// <summary>Null if the survey does not exist.</summary>
    Task<SurveyStatus?> GetStatusAsync(Guid id, CancellationToken ct = default);

    /// <summary>Draft or submitted answers both count.</summary>
    Task<bool> HasSavedAnswersAsync(Guid surveyId, CancellationToken ct = default);

    /// <summary>Stamps <c>UpdatedAt</c>. No-op if the survey does not exist.</summary>
    Task SetStatusAsync(Guid id, SurveyStatus status, Instant updatedAt, CancellationToken ct = default);

    /// <summary>Draft → PendingApproval: stamps <c>SubmittedAt</c> and clears any prior rejection note. No-op if the survey does not exist.</summary>
    Task SubmitForApprovalAsync(Guid id, Instant submittedAt, CancellationToken ct = default);

    /// <summary>PendingApproval → Open: clears <c>SubmittedAt</c>/<c>RejectionNote</c>. No-op if the survey does not exist.</summary>
    Task ApproveAsync(Guid id, Instant approvedAt, CancellationToken ct = default);

    /// <summary>PendingApproval → Draft: records the Board's rejection note and clears <c>SubmittedAt</c>. No-op if the survey does not exist.</summary>
    Task RejectAsync(Guid id, string note, Instant rejectedAt, CancellationToken ct = default);

    /// <summary>Surveys with no invitations are absent.</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetInvitedCountsBySurveyAsync(CancellationToken ct = default);

    /// <summary>Drafts (<c>SubmittedAt is null</c>) excluded.</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetResponseCountsBySurveyAsync(CancellationToken ct = default);

    /// <summary>Only rows whose email was prepared (<c>SentAt != null</c>); public-link participation rows are excluded.</summary>
    Task<IReadOnlySet<Guid>> GetInvitedUserIdsAsync(Guid surveyId, CancellationToken ct = default);

    /// <summary>Unordered: the caller sorts.</summary>
    Task<IReadOnlyList<SurveyInvitation>> GetInvitationsAsync(Guid surveyId, CancellationToken ct = default);

    /// <summary>Commits per invite so the send wave is restartable.</summary>
    Task AddInvitationAndSaveAsync(SurveyInvitation invitation, CancellationToken ct = default);

    /// <summary>
    /// Gets or creates the one per-survey/user participation row. The unique database index is the
    /// authority when concurrent public-start requests race.
    /// </summary>
    Task<SurveyInvitation> GetOrCreateParticipationAsync(
        Guid surveyId, Guid userId, Instant createdAt, CancellationToken ct = default);

    /// <summary>
    /// Due for the one-time 7-day reminder: survey <c>Open</c>, not <c>Completed</c>, no reminder yet, and sent on or
    /// before <paramref name="cutoff"/>.
    /// </summary>
    Task<IReadOnlyList<SurveyInvitation>> GetInvitationsDueForReminderAsync(Instant cutoff, CancellationToken ct = default);

    /// <summary>No-op if the invitation is gone.</summary>
    Task SetReminderSentAsync(Guid invitationId, Instant at, CancellationToken ct = default);

    /// <summary>
    /// A first <c>Queued</c> also stamps <c>SentAt</c>, upgrading a public-link participation row into an emailed
    /// invitation. No-op if the invitation does not exist.
    /// </summary>
    Task UpdateInvitationStatusAsync(Guid id, EmailOutboxStatus status, Instant at, CancellationToken ct = default);

    Task<SurveyInvitation?> GetInvitationByIdAsync(Guid invitationId, CancellationToken ct = default);

    /// <summary><paramref name="slug"/> is already normalised. Null if none.</summary>
    Task<Guid?> GetIdByPublicSlugAsync(string slug, CancellationToken ct = default);

    /// <summary>No-op if the survey does not exist.</summary>
    Task IncrementPublicStartedAsync(Guid surveyId, CancellationToken ct = default);

    /// <summary>The in-progress Identified draft, with answers, or null.</summary>
    Task<SurveyResponse?> GetDraftResponseAsync(Guid surveyId, Guid userId, CancellationToken ct = default);

    Task AddResponseAsync(SurveyResponse response, CancellationToken ct = default);

    /// <summary>
    /// Replaces the draft's answers and stamps the current entry path and culture. No-op if the response is already
    /// submitted.
    /// </summary>
    Task SaveDraftAnswersAsync(
        Guid draftResponseId,
        IReadOnlyList<SurveyAnswer> answers,
        SurveyInputMethod inputMethod,
        string culture,
        CancellationToken ct = default);

    /// <summary>CompletionTracked/Anonymous final submit.</summary>
    Task AddResponseWithAnswersAndSaveAsync(SurveyResponse response, CancellationToken ct = default);

    /// <summary>
    /// Finalises an Identified draft and marks its participation complete in one save. No-op if the
    /// participation is already complete.
    /// </summary>
    Task FinalizeIdentifiedResponseAsync(
        Guid invitationId,
        Guid draftResponseId,
        IReadOnlyList<SurveyAnswer> answers,
        Instant submittedAt,
        SurveyInputMethod inputMethod,
        string culture,
        CancellationToken ct = default);

    /// <summary>
    /// Stores an unlinkable CompletionTracked response, retires any Identified draft, and marks the
    /// participation complete in one save. No-op if the participation is already complete.
    /// </summary>
    Task FinalizeCompletionTrackedResponseAsync(
        Guid invitationId,
        Guid userId,
        SurveyResponse response,
        CancellationToken ct = default);

    /// <summary>No timestamp. No-op if the invitation is gone.</summary>
    Task MarkInvitationStartedAsync(Guid invitationId, CancellationToken ct = default);

    /// <summary>Submitted only (<c>SubmittedAt is not null</c>), with answers. Unordered.</summary>
    Task<IReadOnlyList<SurveyResponse>> GetResponsesForResultsAsync(Guid surveyId, CancellationToken ct = default);

    Task<int> GetStartedInvitationCountAsync(Guid surveyId, CancellationToken ct = default);

    /// <summary>
    /// For the Article 15 export. CompletionTracked/Anonymous responses carry no <c>UserId</c> and are not linkable
    /// personal data, so they are excluded.
    /// </summary>
    Task<IReadOnlyList<SurveyResponse>> GetIdentifiedResponsesForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// For the Article 15 export. Personal data whatever the response's anonymity: for someone only invited, or who
    /// answered CompletionTracked, they are the only record of theirs the section holds.
    /// </summary>
    Task<IReadOnlyList<SurveyInvitation>> GetInvitationsForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// GDPR Art. 17: de-identifies the user's responses — drops the UserId and
    /// InvitationId links and demotes them to <see cref="ResponseAnonymity.Anonymous"/>
    /// — and deletes their invitations. The answers stay as anonymous research data.
    /// </summary>
    Task<int> AnonymizeResponsesForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// For the Article 15 export, in any status: a Draft nobody else can see is still personal data.
    /// </summary>
    Task<IReadOnlyList<Survey>> GetSurveysAuthoredByAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// GDPR Art. 17: drops the authorship link on the person's surveys (<c>CreatedByUserId</c>
    /// to <see cref="Guid.Empty"/>) and clears any Board rejection note, which is prose about
    /// them. The survey and its questions stay — they are the association's own record.
    /// </summary>
    Task<int> ClearAuthorshipForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Moves authorship onto the survivor and stamps <paramref name="now"/>. Without it the author-scoped index hides
    /// the survivor's own surveys.
    /// </summary>
    Task<int> ReassignAuthorshipAsync(
        Guid fromUserId, Guid toUserId, Instant now, CancellationToken ct = default);
}
