using Humans.Base.Interfaces;
using Humans.Surveys.Contracts;
using Humans.Surveys.Data;
using Humans.Surveys.Domain;

namespace Humans.Surveys.Services;

internal sealed class SurveyDefinitionReader(
    ISurveyRepository repo,
    ISurveyInviteTokenProvider tokenProvider) : IApplicationService
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

    public async Task<SurveyOfficialLink?> GetOfficialLinkAsync(
        Guid surveyId,
        Guid userId,
        CancellationToken ct = default)
    {
        var survey = await repo.GetByIdAsync(surveyId, ct);
        if (survey?.Status != SurveyStatus.Open) return null;

        var invitation = (await repo.GetInvitationsAsync(surveyId, ct))
            .FirstOrDefault(candidate =>
                candidate.UserId == userId
                && candidate.SentAt is not null
                && !candidate.Completed);
        if (invitation is not null)
        {
            return new SurveyOfficialLink(tokenProvider.Create(invitation.Id), null);
        }

        return string.IsNullOrWhiteSpace(survey.PublicSlug)
            ? null
            : new SurveyOfficialLink(null, survey.PublicSlug);
    }

    /// <summary>
    /// The machine-readable question graph behind <c>/api/backdoor/surveys/{id}</c>. Built on
    /// <see cref="GetForEditAsync"/> so there is one read path, then flattened: localized text
    /// resolved once in the survey's default culture, and the stored branching payload mirrored
    /// onto <see cref="SurveyBranchCondition"/> so the persisted jsonb shape stays private.
    /// </summary>
    public async Task<SurveyDefinitionSnapshot?> GetDefinitionAsync(Guid surveyId, CancellationToken ct = default)
    {
        var detail = await GetForEditAsync(surveyId, ct);
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
                     q.RankedUnavailableOptionValues?.ToList()))]);
    }

    private static SurveyBranchCondition? ToBranchCondition(BranchCondition? condition) =>
        condition is null
            ? null
            : new SurveyBranchCondition(
                condition.Combine.ToString(),
                [.. condition.Clauses.Select(c => new SurveyBranchClause(
                    c.QuestionId, c.Operator.ToString(), c.OptionValues))]);

    public async Task<SurveyDetail?> GetForEditAsync(Guid surveyId, CancellationToken ct = default)
    {
        var s = await repo.GetByIdAsync(surveyId, ct);
        if (s is null) return null;

        var input = new SurveyEditInput(
            s.Title, s.Intro, s.ThankYou, s.InvitationEmailSubject, s.InvitationEmailMessage,
            s.DefaultCulture, s.AllowAnonymous, s.OpensAt, s.ClosesAt,
            s.AudienceType, s.AudienceTeamId, s.AudienceLoggedInSince, s.PublicSlug,
            SurveyQuestionMapping.ToQuestionInputs(s),
            s.IsAsociadoVote == true);

        return new SurveyDetail(s.Id, s.Status, input, s.CreatedByUserId, s.RejectionNote);
    }
}
