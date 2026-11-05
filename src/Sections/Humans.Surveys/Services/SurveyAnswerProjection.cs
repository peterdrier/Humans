using Humans.Surveys.Contracts;
using Humans.Surveys.Domain;

namespace Humans.Surveys.Services;

internal sealed class SurveyAnswerProjection(Survey? survey, string culture)
{
    private readonly RespondentAnswerContext context = BuildRespondentAnswerContext(survey, culture);

    internal string PromptFor(SurveyAnswer answer) => context.Prompts.GetValueOrDefault(answer.QuestionId, string.Empty);

    private static RespondentAnswerContext BuildRespondentAnswerContext(Survey? survey, string culture)
    {
        var questions = survey?.Questions ?? [];
        return new(
            questions.ToDictionary(
                question => question.Id,
                question => question.Options.ToDictionary(
                    option => option.Value,
                    option => option.Label.Resolve(culture, culture),
                    StringComparer.Ordinal)),
            questions.ToDictionary(
                question => question.Id,
                question => question.Prompt.Resolve(culture, culture)),
            questions.ToDictionary(question => question.Id));
    }

    internal IReadOnlyList<RespondentAnswer> BuildRespondentAnswers(SurveyResponse response) =>
        response.Answers
            .Select(answer => new RespondentAnswer(
                answer.QuestionId,
                context.Prompts.GetValueOrDefault(answer.QuestionId, string.Empty),
                ResolveSelectedLabels(answer),
                answer.TextValue,
                answer.RatingValue,
                ResolveGridSelections(answer),
                ResolveRankedBallot(answer)))
            .ToList();

    internal IReadOnlyList<string> ResolveSelectedLabels(SurveyAnswer answer)
    {
        if (answer.SelectedOptionValues.Count == 0) return [];
        var labels = context.OptionLabels.GetValueOrDefault(answer.QuestionId);
        return answer.SelectedOptionValues
            .Select(v => labels is not null && labels.TryGetValue(v, out var label) ? label : v)
            .ToList();
    }

    internal IReadOnlyList<ResolvedGridSelection> ResolveGridSelections(SurveyAnswer answer)
    {
        if (answer.GridSelections is null
            || !context.QuestionsById.TryGetValue(answer.QuestionId, out var question)) return [];

        var rows = (question.GridRows ?? []).ToDictionary(
            row => row.Value,
            row => row.Label.Resolve(culture, culture),
            StringComparer.Ordinal);
        var columns = question.Options.ToDictionary(
            option => option.Value,
            option => option.Label.Resolve(culture, culture),
            StringComparer.Ordinal);
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

        var labels = context.OptionLabels.GetValueOrDefault(answer.QuestionId);
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
        IReadOnlyDictionary<Guid, SurveyQuestion> QuestionsById);
}
