using Humans.Google.Contracts;
using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Humans.Surveys.Data;
using Humans.Surveys.Contracts;
using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Surveys.Services;

internal sealed class SurveyService : ISurveyService, IUserDataContributor, IUserMerge
{
    private readonly ISurveyRepository repo;
    private readonly ILogger<SurveyService> logger;
    private readonly SurveyInvitationDelivery invitationDelivery;
    private readonly SurveyReview review;
    private readonly SurveyDefinitionReader definitionReader;
    private readonly SurveyResultsReader resultsReader;
    private readonly SurveyDataExport dataExport;
    private readonly SurveyAuthoring authoring;
    private readonly SurveyTranslationFiller translationFiller;
    private readonly SurveyResponseWorkflow responses;

    public SurveyService(
        ISurveyRepository repo,
        IAuditLogService auditLog,
        IClock clock,
        ILogger<SurveyService> logger,
        IUserService userService,
        SurveyAudience audience,
        SurveyInvitationDelivery invitationDelivery,
        ISurveyInviteTokenProvider tokenProvider,
        IGoogleTranslationService translation,
        IFileStorage fileStorage)
    {
        this.repo = repo;
        this.logger = logger;
        this.invitationDelivery = invitationDelivery;
        review = new(repo, auditLog, clock, audience, invitationDelivery);
        definitionReader = new(repo, tokenProvider, userService);
        resultsReader = new(repo, userService, auditLog);
        dataExport = new(repo, userService);
        authoring = new(repo, auditLog, clock, fileStorage, logger);
        translationFiller = new(translation);
        responses = new(repo, clock, logger, userService, audience, tokenProvider, definitionReader);
    }

    internal const string SurveyResponses = SurveyDataExport.SurveyResponses;
    internal const string AuthoredSurveys = SurveyDataExport.AuthoredSurveys;
    internal const string SurveyInvitations = SurveyDataExport.SurveyInvitations;

    public Task<IReadOnlyList<SurveySummary>> GetSummariesAsync(CancellationToken ct = default) =>
        definitionReader.GetSummariesAsync(ct);

    public Task<SurveyOfficialLink?> GetOfficialLinkAsync(Guid surveyId, Guid userId, CancellationToken ct = default) =>
        definitionReader.GetOfficialLinkAsync(surveyId, userId, ct);

    public Task<SurveyDefinitionSnapshot?> GetDefinitionAsync(Guid surveyId, CancellationToken ct = default) =>
        definitionReader.GetDefinitionAsync(surveyId, ct);

    public Task<SurveyDetail?> GetForEditAsync(Guid surveyId, CancellationToken ct = default) =>
        definitionReader.GetForEditAsync(surveyId, ct);

    public Task<bool> HasSavedAnswersAsync(Guid surveyId, CancellationToken ct = default)
        => repo.HasSavedAnswersAsync(surveyId, ct);

    public Task<SurveySaveResult> CreateAsync(SurveyEditInput input, Guid actorUserId, CancellationToken ct = default) =>
        authoring.CreateAsync(input, actorUserId, ct);

    public Task<SurveySaveResult> UpdateAsync(Guid surveyId, SurveyEditInput input, SurveyViewer viewer, CancellationToken ct = default) =>
        authoring.UpdateAsync(surveyId, input, viewer, ct);

    public async Task<int> PreFillTranslationsAsync(
        Guid surveyId, IReadOnlyList<string> targetCultures, SurveyViewer viewer, CancellationToken ct = default)
    {
        var detail = await GetForEditAsync(surveyId, ct)
            ?? throw new InvalidOperationException("Survey not found.");
        if (detail.Editable.IsAsociadoVote && detail.Status != SurveyStatus.Draft)
        {
            throw new InvalidOperationException(
                "An Asociado vote cannot be edited after it has opened.");
        }
        var (input, filled) = await translationFiller.FillAsync(detail.Editable, targetCultures, ct);
        if (filled == 0) return 0;

        (await UpdateAsync(surveyId, input, viewer, ct)).RequireSuccess();
        logger.LogInformation(
            "Survey {SurveyId}: pre-filled {Count} missing translations from {Source}",
            surveyId, filled, detail.Editable.DefaultCulture);
        return filled;
    }

    public Task<SurveyReviewResult> OpenAsync(Guid surveyId, Guid actorUserId, CancellationToken ct = default) =>
        review.OpenAsync(surveyId, actorUserId, ct);

    public Task<SurveyReviewResult> CloseAsync(Guid surveyId, Guid actorUserId, CancellationToken ct = default) =>
        review.CloseAsync(surveyId, actorUserId, ct);

    public Task<IReadOnlyList<SurveyAdminSummary>> GetAdminSummariesAsync(SurveyViewer viewer, CancellationToken ct = default) =>
        definitionReader.GetAdminSummariesAsync(viewer, ct);

    public Task<IReadOnlyList<SurveyPendingApprovalItem>> GetPendingApprovalQueueAsync(CancellationToken ct = default) =>
        definitionReader.GetPendingApprovalQueueAsync(ct);

    public Task<SurveySubmitResult> SubmitForApprovalAsync(Guid surveyId, Guid actorUserId, CancellationToken ct = default) =>
        review.SubmitForApprovalAsync(surveyId, actorUserId, ct);

    public Task<SurveyReviewResult> ApproveAndSendAsync(Guid surveyId, SurveyViewer viewer, CancellationToken ct = default) =>
        review.ApproveAndSendAsync(surveyId, viewer, ct);

    public Task<SurveyReviewResult> RejectAsync(Guid surveyId, SurveyViewer viewer, string note, CancellationToken ct = default) =>
        review.RejectAsync(surveyId, viewer, note, ct);

    public Task<int> PreviewAudienceCountAsync(Guid surveyId, CancellationToken ct = default) =>
        invitationDelivery.PreviewAudienceCountAsync(surveyId, ct);

    public Task<SendResult> SendInvitesAsync(Guid surveyId, Guid actorUserId, CancellationToken ct = default) =>
        invitationDelivery.SendInvitesAsync(surveyId, actorUserId, ct);

    public Task<IReadOnlyList<SurveyInviteStatus>> GetInviteStatusesAsync(Guid surveyId, CancellationToken ct = default) =>
        invitationDelivery.GetInviteStatusesAsync(surveyId, ct);

    public Task<SurveyAnswerContext?> ResolveAnswerContextAsync(string token, CancellationToken ct = default) =>
        responses.ResolveAnswerContextAsync(token, ct);

    public Task<bool> IsEligibleAsociadoAsync(Guid userId, CancellationToken ct = default) =>
        responses.IsEligibleAsociadoAsync(userId, ct);

    public Task<Guid> StartIdentifiedDraftAsync(
        Guid surveyId, Guid participationId, Guid userId, SurveyInputMethod inputMethod,
        string culture, CancellationToken ct = default) =>
        responses.StartIdentifiedDraftAsync(surveyId, participationId, userId, inputMethod, culture, ct);

    public Task<SurveyPublicStart?> StartPublicTrackedResponseAsync(
        Guid surveyId, Guid userId, ResponseAnonymity anonymity, string culture, CancellationToken ct = default) =>
        responses.StartPublicTrackedResponseAsync(surveyId, userId, anonymity, culture, ct);

    public Task MarkInvitationStartedAsync(Guid invitationId, CancellationToken ct = default) =>
        responses.MarkInvitationStartedAsync(invitationId, ct);

    public Task<SurveyPublicContext?> ResolvePublicContextAsync(string slug, Guid? userId, CancellationToken ct = default) =>
        responses.ResolvePublicContextAsync(slug, userId, ct);

    public Task IncrementPublicStartedAsync(Guid surveyId, CancellationToken ct = default) =>
        responses.IncrementPublicStartedAsync(surveyId, ct);

    public Task SaveDraftAnswersAsync(
        Guid draftResponseId, IReadOnlyList<SurveyAnswerInput> answers, SurveyInputMethod inputMethod,
        string culture, CancellationToken ct = default) =>
        responses.SaveDraftAnswersAsync(draftResponseId, answers, inputMethod, culture, ct);

    /// <inheritdoc />
    public Task<SurveyWizardAdvanceResult> AdvanceWizardAsync(
        SurveyWizardState state, int page, bool back, IReadOnlyList<SurveyAnswerInput> postedAnswers,
        CancellationToken ct = default) =>
        responses.AdvanceWizardAsync(state, page, back, postedAnswers, ct);

    public Task<SurveyResultsView?> GetResultsAsync(Guid surveyId, CancellationToken ct = default) =>
        resultsReader.GetResultsAsync(surveyId, ct);

    public Task<SurveyScopedResults?> GetScopedResultsAsync(
        Guid surveyId, SurveyResultsScope scope, CancellationToken ct = default) =>
        resultsReader.GetScopedResultsAsync(surveyId, scope, ct);

    public Task<SurveyResponseExport?> GetResponseExportAsync(
        Guid surveyId, Guid actorUserId, CancellationToken ct = default) =>
        resultsReader.GetResponseExportAsync(surveyId, actorUserId, ct);

    public async Task<SurveySaveResult> SetRankedAvailabilityAsync(
        Guid surveyId,
        Guid questionId,
        IReadOnlyList<string> unavailableValues,
        SurveyViewer viewer,
        CancellationToken ct = default)
    {
        var detail = await GetForEditAsync(surveyId, ct);
        if (detail is null)
            return new(Error: new(null, "Survey not found."));
        if (detail.Status != SurveyStatus.Closed)
            return new(Error: new(null, "Candidate availability can only change after the vote closes."));
        var question = detail.Editable.Questions.FirstOrDefault(candidate => candidate.Id == questionId);
        if (question?.Type != SurveyQuestionType.RankedChoice)
            return new(Error: new(null, "Ranked-choice question not found."));
        var known = question.Options.Select(option => option.Value).ToHashSet(StringComparer.Ordinal);
        var normalized = unavailableValues
            .Where(known.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var updatedQuestions = detail.Editable.Questions
            .Select(candidate => candidate.Id == questionId
                ? candidate with { RankedUnavailableOptionValues = normalized }
                : candidate)
            .ToList();
        return await authoring.UpdateCoreAsync(
            surveyId,
            detail.Editable with { Questions = updatedQuestions },
            viewer,
            allowRankedAvailabilityChanges: true,
            ct);
    }

    /// <summary>
    /// Only Identified responses carry a <c>UserId</c>; for CompletionTracked/Anonymous respondents and the merely
    /// invited, the invitation slice is the record instead. Every slice is always emitted (empty, never null) so the
    /// export keys stay stable.
    /// </summary>
    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        dataExport.ContributeForUserAsync(userId, ct);

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [SurveyResponses] =
                "Partially retained: the invitation is deleted and the response is severed from " +
                "the person (UserId and InvitationId dropped, Anonymity forced to Anonymous), but " +
                "the answers themselves survive as an anonymous data point in the survey's " +
                "results — GDPR Art. 17(3)(b). They are no longer attributable to anyone.",
            [AuthoredSurveys] =
                "Partially retained: the authorship link is dropped and any rejection note " +
                "deleted, but the survey and its questions survive as the association's own " +
                "record of what it asked — GDPR Art. 17(3)(b).",
            [SurveyInvitations] =
                "Erased: AnonymizeResponsesForUserAsync deletes the person's invitation rows " +
                "outright, so nothing of this section's record that they were asked survives."
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>
    /// The identity link is dropped and the response demoted to Anonymous — the
    /// answers survive as anonymous research data that is no longer personal data.
    /// </summary>
    public async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        await repo.AnonymizeResponsesForUserAsync(userId, ct);
        await repo.ClearAuthorshipForUserAsync(userId, ct);
    }

    /// <summary>
    /// Account merge: authorship follows the survivor. Responses and invitations retain
    /// their original account IDs; GDPR export follows the identity chain, and the deletion
    /// pipeline erases each account in that chain.
    /// </summary>
    public Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct)
        => repo.ReassignAuthorshipAsync(mergedFromUserId, mergedToUserId, now, ct);


}
