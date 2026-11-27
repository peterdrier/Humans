using Humans.Surveys.Contracts;
using Humans.Surveys.Domain;

namespace Humans.Surveys.Services;

internal sealed class SurveyAnswerProjection(Survey? survey, string culture)
{
    private readonly RespondentAnswerContext _context = BuildRespondentAnswerContext(survey, culture);

    internal string PromptFor(SurveyAnswer answer) => _context.Prompts.GetValueOrDefault(answer.QuestionId, string.Empty);

    private static RespondentAnswerContext BuildRespondentAnswerContext(Survey? survey, string culture)
    {
        var questions = survey?.Questions ?? [];
        var defaultCulture = survey?.DefaultCulture ?? culture;
        return new(
            questions.ToDictionary(
                question => question.Id,
                question => question.Options.ToDictionary(
                    option => option.Value,
                    option => option.Label.Resolve(culture, defaultCulture),
                    StringComparer.Ordinal)),
            questions.ToDictionary(
                question => question.Id,
                question => question.Prompt.Resolve(culture, defaultCulture)),
            questions.ToDictionary(question => question.Id),
            defaultCulture);
    }

    internal IReadOnlyList<RespondentAnswer> BuildRespondentAnswers(SurveyResponse response) =>
        response.Answers
            .OrderBy(answer => _context.QuestionsById.TryGetValue(answer.QuestionId, out var q) ? q.PageNumber : int.MaxValue)
            .ThenBy(answer => _context.QuestionsById.TryGetValue(answer.QuestionId, out var q) ? q.Order : int.MaxValue)
            .Select(answer => new RespondentAnswer(
                answer.QuestionId,
                _context.Prompts.GetValueOrDefault(answer.QuestionId, string.Empty),
                ResolveSelectedLabels(answer),
                answer.TextValue,
                answer.RatingValue,
                ResolveGridSelections(answer),
                ResolveRankedBallot(answer)))
            .ToList();

    internal IReadOnlyList<string> ResolveSelectedLabels(SurveyAnswer answer)
    {
        if (answer.SelectedOptionValues.Count == 0) return [];
        var labels = _context.OptionLabels.GetValueOrDefault(answer.QuestionId);
        return answer.SelectedOptionValues
            .Select(v => labels is not null && labels.TryGetValue(v, out var label) ? label : v)
            .ToList();
    }

    internal IReadOnlyList<ResolvedGridSelection> ResolveGridSelections(SurveyAnswer answer)
    {
        if (answer.GridSelections is null
            || !_context.QuestionsById.TryGetValue(answer.QuestionId, out var question)) return [];

        var rows = (question.GridRows ?? []).ToDictionary(
            row => row.Value,
            row => row.Label.Resolve(culture, _context.DefaultCulture),
            StringComparer.Ordinal);
        var columns = _context.OptionLabels[answer.QuestionId];
        return answer.GridSelections
            .Where(selection => selection.Value.Count > 0)
            .Select(selection => new ResolvedGridSelection(
                selection.Key,
                rows.GetValueOrDefault(selection.Key, selection.Key),
                selection.Value.ToList(),
                selection.Value
                    .Select(value => columns.GetValueOrDefault(value, value))
                    .ToList()))
            .ToList();
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>>? CopyGridSelections(SurveyAnswer answer)
        => answer.GridSelections?.ToDictionary(
            selection => selection.Key,
            selection => (IReadOnlyList<string>)selection.Value.ToList(),
            StringComparer.Ordinal);

    internal static SurveyRankedBallot? CopyRankedBallot(SurveyAnswer answer)
        => answer.RankedValue is null
            ? null
            : new SurveyRankedBallot(
                [.. answer.RankedValue.RankGroups.Select(group => (IReadOnlyList<string>)[.. group])],
                [.. answer.RankedValue.Rejected]);

    internal ResolvedRankedBallot? ResolveRankedBallot(SurveyAnswer answer)
    {
        if (answer.RankedValue is null) return null;

        var labels = _context.OptionLabels.GetValueOrDefault(answer.QuestionId);
        string Resolve(string value) =>
            labels is not null && labels.TryGetValue(value, out var label) ? label : value;

        return new ResolvedRankedBallot(
            [.. answer.RankedValue.RankGroups
                .Select(group => (IReadOnlyList<string>)[.. group.Select(Resolve)])],
            [.. answer.RankedValue.Rejected.Select(Resolve)]);
    }

    private sealed record RespondentAnswerContext(
        IReadOnlyDictionary<Guid, Dictionary<string, string>> OptionLabels,
        IReadOnlyDictionary<Guid, string> Prompts,
        IReadOnlyDictionary<Guid, SurveyQuestion> QuestionsById,
        string DefaultCulture);
}
