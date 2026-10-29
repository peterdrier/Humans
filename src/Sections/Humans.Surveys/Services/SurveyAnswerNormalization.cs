using Humans.Surveys.Contracts;
using Humans.Surveys.Domain;

namespace Humans.Surveys.Services;

internal static class SurveyAnswerNormalization
{
    internal static (SurveyAnswerInput Answer, string? Refusal) NormalizeAnswer(QuestionInput question, SurveyAnswerInput answer)
    {
        var choices = question.Type is SurveyQuestionType.SingleChoice or SurveyQuestionType.MultiChoice
            ? answer.SelectedOptionValues
                .Where(value => question.Options.Any(option => string.Equals(option.Value, value, StringComparison.Ordinal)))
                .Where(value => !string.IsNullOrEmpty(value))
                .Distinct(StringComparer.Ordinal)
                .ToList()
            : [];
        if (question.Type == SurveyQuestionType.SingleChoice && choices.Count > 1)
            return (answer, "A single-choice question permits only one option.");

        var rating = question.Type == SurveyQuestionType.Rating ? answer.RatingValue : null;
        if (rating is { } value && (value < (question.RatingMin ?? 1) || value > (question.RatingMax ?? 5)))
            return (answer, "The rating is outside the question's range.");

        var text = question.Type is SurveyQuestionType.ShortText or SurveyQuestionType.LongText
            && !string.IsNullOrWhiteSpace(answer.TextValue) ? answer.TextValue : null;
        if (text is not null && text.EnumerateRunes().Count() > 4000)
            return (answer, "A text answer must be 4000 characters or fewer.");

        var grid = question.Type == SurveyQuestionType.Grid
            ? NormalizeGridSelections(question.GridRows, question.Options, question.GridSelectionMode, answer.GridSelections)
            : null;
        (RankedAnswer? Answer, string? Refusal) ranked = question.Type == SurveyQuestionType.RankedChoice
            ? NormalizeRankedAnswer(question, answer.RankedValue)
            : (null, null);
        if (ranked.Refusal is { } rankedRefusal) return (answer, rankedRefusal);

        var normalized = answer with
        {
            SelectedOptionValues = choices,
            TextValue = text,
            RatingValue = rating,
            GridSelections = grid?.Count > 0
                ? grid.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.Ordinal)
                : null,
            RankedValue = ranked.Answer,
        };
        return (normalized, null);
    }

    private static (RankedAnswer? Answer, string? Refusal) NormalizeRankedAnswer(QuestionInput question, RankedAnswer? answer)
    {
        if (answer is null) return (null, null);
        var authored = question.Options.OrderBy(option => option.Order).Select(option => option.Value).ToList();
        return NormalizeRankedAnswer(authored, question.RankedSettings, answer);
    }

    private static (RankedAnswer? Answer, string? Refusal) NormalizeRankedAnswer(
        IReadOnlyList<string> authored,
        RankedQuestionSettings? settings,
        RankedAnswer answer)
    {
        settings ??= RankedQuestionSettings.Default;
        var known = authored.ToHashSet(StringComparer.Ordinal);
        var suppliedKnownValues = answer.RankGroups
            .SelectMany(group => group)
            .Concat(answer.Rejected)
            .Where(known.Contains)
            .ToList();
        if (suppliedKnownValues.Distinct(StringComparer.Ordinal).Count() != suppliedKnownValues.Count)
            return (null, "A ranked-choice option may appear only once in a ballot.");

        var authoredPositions = authored
            .Select((value, index) => (value, index))
            .ToDictionary(pair => pair.value, pair => pair.index, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<IReadOnlyList<string>>();
        foreach (var group in answer.RankGroups)
        {
            var valid = group
                .Where(value => known.Contains(value) && seen.Add(value))
                .OrderBy(value => authoredPositions[value])
                .ToList();
            if (!settings.AllowEqualRanks && valid.Count > 1)
                return (null, "This ranked-choice question does not allow equal ranks.");
            if (valid.Count > 0) groups.Add(valid);
        }

        var rejected = answer.Rejected
            .Where(value => known.Contains(value) && seen.Add(value))
            .OrderBy(value => authoredPositions[value])
            .ToList();
        if (!settings.AllowReject && rejected.Count > 0)
            return (null, "This ranked-choice question does not allow rejection.");
        return (groups.Count == 0 && rejected.Count == 0 ? null : new RankedAnswer(groups, rejected), null);
    }

    internal static Dictionary<string, List<string>> NormalizeGridSelections(
        IReadOnlyList<GridRowInput>? rows,
        IReadOnlyList<OptionInput> columns,
        GridSelectionMode? mode,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? selections)
    {
        if (rows is null || mode is null || selections is null)
        {
            return new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }

        var rowValues = rows.Select(row => row.Value).ToHashSet(StringComparer.Ordinal);
        var columnValues = columns.Select(column => column.Value).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (rowValue, selectedColumns) in selections)
        {
            if (!rowValues.Contains(rowValue)) continue;
            var valid = selectedColumns
                .Where(columnValues.Contains)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (mode == GridSelectionMode.Single && valid.Count > 1)
            {
                valid = valid.Take(1).ToList();
            }

            if (valid.Count > 0) result[rowValue] = valid;
        }

        return result;
    }
}
