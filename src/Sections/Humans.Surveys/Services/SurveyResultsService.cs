using Humans.AuditLog.Contracts;
using Humans.Users.Contracts;
using Humans.Surveys.Data;
using Humans.Surveys.Contracts;
using Humans.Surveys.Domain;

namespace Humans.Surveys.Services;

/// <summary>Read-only survey analysis: summaries, the question graph, results and the response export.</summary>
internal sealed class SurveyResultsService(
    ISurveyRepository repo,
    ISurveyService authoring,
    IAuditLogService auditLog,
    IUserService userService) : ISurveyResults
{
    public async Task<IReadOnlyList<SurveySummary>> GetSummariesAsync(CancellationToken ct = default)
    {
        var surveys = await repo.GetAllSummariesAsync(ct);
        var invited = await repo.GetInvitedCountsBySurveyAsync(ct);
        var responses = await repo.GetResponseCountsBySurveyAsync(ct);

        return surveys.Select(s => new SurveySummary(
            s.Id,
            s.Title.Resolve(s.DefaultCulture, s.DefaultCulture),
            s.Status,
            invited.GetValueOrDefault(s.Id),
            responses.GetValueOrDefault(s.Id),
            s.CreatedByUserId)).ToList();
    }


    /// <summary>
    /// The machine-readable question graph behind <c>/api/backdoor/surveys/{id}</c>. Built on
    /// <see cref="ISurveyService.GetForEditAsync"/> so there is one read path, then flattened: localized text
    /// resolved once in the survey's default culture, and the stored branching payload mirrored
    /// onto <see cref="SurveyBranchCondition"/> so the persisted jsonb shape stays private.
    /// </summary>
    public async Task<SurveyDefinitionSnapshot?> GetDefinitionAsync(Guid surveyId, CancellationToken ct = default)
    {
        var detail = await authoring.GetForEditAsync(surveyId, ct);
        if (detail is null) return null;

        var e = detail.Editable;
        var culture = e.DefaultCulture;

        return new SurveyDefinitionSnapshot(
            detail.Id,
            e.Title.Resolve(culture, culture),
            detail.Status,
            culture,
            [.. e.Questions
                .OrderBy(q => q.PageNumber)
                .ThenBy(q => q.Order)
                .Select(q => new SurveyDefinitionQuestion(
                    // Persisted questions always carry an id; the nullable is the authoring
                    // shape, where a not-yet-saved question has none.
                    q.Id ?? Guid.Empty,
                    q.PageNumber,
                    q.Order,
                    q.Type,
                    q.Prompt.Resolve(culture, culture),
                    q.Type == SurveyQuestionType.Information ? q.HelpText.Resolve(culture, culture) : null,
                    q.IsRequired,
                    q.RatingMin,
                    q.RatingMax,
                    ToBranchCondition(q.ShowIf),
                    q.GridSelectionMode,
                    [.. (q.GridRows ?? [])
                        .Select(row => new SurveyExportGridRow(row.Value, row.Label.Resolve(culture, culture)))],
                    [.. (q.InformationImages ?? [])
                        .Where(image => !string.IsNullOrWhiteSpace(image.StoragePath))
                        .Select(image => new SurveyDefinitionImage(
                            image.Id ?? Guid.Empty,
                            $"/{image.StoragePath!.TrimStart('/')}",
                            image.Label.Resolve(culture, culture),
                            image.AltText.Resolve(culture, culture)))],
                     [.. q.Options
                         .OrderBy(o => o.Order)
                         .Select(o => new SurveyExportOption(o.Value, o.Label.Resolve(culture, culture)))],
                     SurveyQuestionMapping.ToRankedSettings(q.RankedSettings),
                     q.RankedUnavailableOptionValues?.ToList(),
                     q.Comment is { } comment
                         ? new SurveyDefinitionComment(comment.Prompt.Resolve(culture, culture), comment.OptionValues)
                         : null))]);
    }

    private static SurveyBranchCondition? ToBranchCondition(BranchCondition? condition) =>
        condition is null
            ? null
            : new SurveyBranchCondition(
                condition.Combine.ToString(),
                [.. condition.Clauses.Select(c => new SurveyBranchClause(
                    c.QuestionId, c.Operator.ToString(), c.OptionValues))]);

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
                .Select(q => SurveyQuestionResults.WithComments(SurveyQuestionResults.WithFollowUp(
                    SurveyQuestionResults.BuildQuestionAggregate(q, selectedResponses, culture) with
                    {
                        PageNumber = q.PageNumber,
                        Order = q.Order,
                        HelpText = q.HelpText.Resolve(culture, culture),
                    },
                    q, survey.Questions, selectedResponses, culture), q, selectedResponses, culture))
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
                 q.RankedUnavailableOptionValues?.ToList(),
                 q.Comment?.Prompt.Resolve(culture, culture)))
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
