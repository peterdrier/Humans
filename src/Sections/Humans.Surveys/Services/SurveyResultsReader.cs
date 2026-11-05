using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Surveys.Contracts;
using Humans.Surveys.Data;
using Humans.Surveys.Domain;
using Humans.Users.Contracts;

namespace Humans.Surveys.Services;

internal sealed class SurveyResultsReader(
    ISurveyRepository repo,
    IUserService userService,
    IAuditLogService auditLog) : IApplicationService
{
    public async Task<SurveyResultsView?> GetResultsAsync(Guid surveyId, CancellationToken ct = default)
    {
        var scoped = await GetScopedResultsAsync(surveyId, SurveyResultsScope.Combined, ct);
        return scoped is { IsEmbargoed: false } ? scoped.Results : null;
    }

    public async Task<SurveyScopedResults?> GetScopedResultsAsync(
        Guid surveyId,
        SurveyResultsScope scope,
        CancellationToken ct = default)
    {
        var survey = await repo.GetByIdAsync(surveyId, ct);
        if (survey is null) return null;

        var culture = survey.DefaultCulture;
        var responses = await repo.GetResponsesForResultsAsync(surveyId, ct);
        var embargoed = survey.IsAsociadoVote == true && survey.Status != SurveyStatus.Closed;
        var selectedResponses = responses
            .Where(response => MatchesScope(response.Anonymity, scope))
            .ToList();
        var invitations = await repo.GetInvitationsAsync(surveyId, ct);
        var sentInvitations = invitations.Where(invitation => invitation.SentAt.HasValue).ToList();
        var invitedCount = sentInvitations.Count;
        var responseCount = responses.Count;
        var completedInvitationCount = sentInvitations.Count(invitation => invitation.Completed);
        var responseRate = invitedCount == 0 ? 0d : (double)completedInvitationCount / invitedCount;

        var questions = embargoed
            ? []
            : survey.Questions
                .Where(q => q.Type != SurveyQuestionType.Information)
                .Select(q => SurveyQuestionResults.BuildQuestionAggregate(q, selectedResponses, culture) with
                {
                    PageNumber = q.PageNumber,
                    Order = q.Order,
                })
                .ToList();

        var funnel = new SurveyFunnel(
            LinkStarted: invitations.Count(invitation => invitation.Started),
            LinkFinished: responses.Count(r => r.InputMethod == SurveyInputMethod.UserSpecificLink),
            SlugStarted: survey.PublicStartedCount,
            SlugFinished: responses.Count(r => r.InputMethod == SurveyInputMethod.Slug));

        var identified = embargoed || survey.IsAsociadoVote == true
            ? []
            : BuildIdentifiedRespondents(survey, responses, culture);
        var unattributedBallots = !embargoed && survey.IsAsociadoVote == true
            ? BuildUnattributedBallots(survey, selectedResponses, culture)
            : [];
        var rankedQuestions = embargoed
            ? new Dictionary<Guid, RankedQuestionResult>()
            : survey.Questions
                .Where(question => question.Type == SurveyQuestionType.RankedChoice)
                .ToDictionary(
                    question => question.Id,
                    question => SurveyQuestionResults.BuildRankedQuestionResult(question, selectedResponses, culture));

        return new SurveyScopedResults(
            new SurveyResultsView(
                surveyId,
                survey.Title.Resolve(culture, culture),
                survey.Status,
                invitedCount,
                responseCount,
                responseRate,
                funnel,
                questions,
                identified),
            embargoed ? 0 : selectedResponses.Count,
            scope,
            embargoed,
            rankedQuestions,
            survey.IsAsociadoVote == true,
            unattributedBallots);
    }

    private static bool MatchesScope(ResponseAnonymity anonymity, SurveyResultsScope scope) => scope switch
    {
        SurveyResultsScope.Unique => anonymity is ResponseAnonymity.Identified or ResponseAnonymity.CompletionTracked,
        SurveyResultsScope.Anonymous => anonymity == ResponseAnonymity.Anonymous,
        _ => true,
    };

    public async Task<SurveyResponseExport?> GetResponseExportAsync(Guid surveyId, Guid actorUserId, CancellationToken ct = default)
    {
        var survey = await repo.GetByIdAsync(surveyId, ct);
        if (survey is null) return null;
        if (survey.IsAsociadoVote == true && survey.Status != SurveyStatus.Closed) return null;

        var culture = survey.DefaultCulture;
        var responses = await repo.GetResponsesForResultsAsync(surveyId, ct);

        var orderedQuestions = survey.Questions
            .Where(q => q.Type != SurveyQuestionType.Information)
            .OrderBy(q => q.PageNumber).ThenBy(q => q.Order)
            .ToList();

        var questions = orderedQuestions
            .Select(q => new SurveyExportQuestion(
                q.Id,
                q.Prompt.Resolve(culture, culture),
                q.Type,
                q.Options.OrderBy(o => o.Order)
                    .Select(o => new SurveyExportOption(o.Value, o.Label.Resolve(culture, culture)))
                    .ToList(),
                q.GridSelectionMode,
                 q.GridRows?.Select(row => new SurveyExportGridRow(
                     row.Value,
                     row.Label.Resolve(culture, culture))).ToList(),
                 SurveyQuestionMapping.ToRankedSettings(q.RankedSettings),
                 q.RankedUnavailableOptionValues?.ToList()))
            .ToList();

        var answerContext = new SurveyAnswerProjection(survey, culture);

        // Identity is resolved only for Identified rows in ordinary surveys. Asociado exports never
        // expose identity, timestamps or submission order, including for legacy Identified rows.
        var identifiedUserIds = responses
            .Where(r => survey.IsAsociadoVote != true
                && r.Anonymity == ResponseAnonymity.Identified
                && r.UserId.HasValue)
            .Select(r => r.UserId!.Value)
            .Distinct()
            .ToList();
        var users = identifiedUserIds.Count == 0
            ? new Dictionary<Guid, UserInfo>()
            : await userService.GetUserInfosAsync(identifiedUserIds, ct);

        var rows = (survey.IsAsociadoVote == true
                ? responses.OrderBy(r => r.Id)
                : responses.OrderBy(r => r.SubmittedAt))
            .Select(r =>
            {
                Guid? userId = null;
                string? userName = null;
                if (survey.IsAsociadoVote != true
                    && r.Anonymity == ResponseAnonymity.Identified
                    && r.UserId is { } id)
                {
                    userId = id;
                    userName = users.TryGetValue(id, out var user) ? user.BurnerName : id.ToString();
                }

                var answers = r.Answers
                    .Select(a => new SurveyExportAnswer(
                        a.QuestionId,
                        a.SelectedOptionValues,
                        answerContext.ResolveSelectedLabels(a),
                        a.TextValue,
                        a.RatingValue,
                        SurveyAnswerProjection.CopyGridSelections(a),
                         answerContext.ResolveGridSelections(a),
                         SurveyAnswerProjection.CopyRankedBallot(a)))
                    .ToList();

                return new SurveyExportRow(
                    r.Id, r.Anonymity, r.InputMethod, r.Culture,
                    survey.IsAsociadoVote == true ? null : r.SubmittedAt, userId, userName, answers);
            })
            .ToList();

        await auditLog.LogAsync(SurveysAuditActions.SurveyResponsesExported, "Survey", surveyId,
            $"Survey responses exported: {rows.Count} rows", actorUserId);
        return new SurveyResponseExport(surveyId, survey.Title.Resolve(culture, culture), culture, questions, rows);
    }

    /// <summary>Builds the Identified-only drill-down; views render identities by user ID. Other tiers never appear (no identity exposure).</summary>
    private static IReadOnlyList<RespondentDetail> BuildIdentifiedRespondents(
        Survey survey, IReadOnlyList<SurveyResponse> responses, string culture)
    {
        var identified = responses
            .Where(r => r.Anonymity == ResponseAnonymity.Identified && r.UserId.HasValue)
            .ToList();
        if (identified.Count == 0) return [];

        var answerContext = new SurveyAnswerProjection(survey, culture);

        return identified
            .Select(r =>
            {
                var answers = answerContext.BuildRespondentAnswers(r);
                return new RespondentDetail(r.UserId!.Value, r.SubmittedAt, answers) { ResponseId = r.Id };
            })
            .ToList();
    }

    private static IReadOnlyList<UnattributedBallotDetail> BuildUnattributedBallots(
        Survey survey, IReadOnlyList<SurveyResponse> responses, string culture)
    {
        var answerContext = new SurveyAnswerProjection(survey, culture);
        return responses
            .OrderBy(response => response.Id)
            .Select(response => new UnattributedBallotDetail(
                answerContext.BuildRespondentAnswers(response)))
            .ToList();
    }

}
