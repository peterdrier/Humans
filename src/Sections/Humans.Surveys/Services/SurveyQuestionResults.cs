using Humans.Surveys.Contracts;
using Humans.Surveys.Domain;

namespace Humans.Surveys.Services;

internal static class SurveyQuestionResults
{
    /// <summary>Aggregates one question across the submitted responses per its type (counts/distribution/free-text).</summary>
    internal static QuestionAggregate BuildQuestionAggregate(
        SurveyQuestion question, IReadOnlyList<SurveyResponse> responses, string culture)
    {
        var prompt = question.Prompt.Resolve(culture, culture);
        var answers = responses
            .SelectMany(r => r.Answers)
            .Where(a => a.QuestionId == question.Id)
            .ToList();

        switch (question.Type)
        {
            case SurveyQuestionType.SingleChoice:
            case SurveyQuestionType.MultiChoice:
                {
                    // Percent base = respondents who answered THIS question, not all submissions —
                    // branched/optional questions aren't seen by everyone, so the total would skew low.
                    var answeredCount = answers.Count(a => a.SelectedOptionValues.Count > 0);
                    var optionCounts = question.Options
                        .Select(o =>
                        {
                            var count = answers.Count(a => a.SelectedOptionValues.Contains(o.Value, StringComparer.Ordinal));
                            var percent = answeredCount == 0 ? 0d : (double)count / answeredCount * 100d;
                            return new OptionCount(o.Value, o.Label.Resolve(culture, culture), count, percent) { Order = o.Order };
                        })
                        .ToList();
                    return new QuestionAggregate(question.Id, prompt, question.Type, optionCounts, [], null, []);
                }

            case SurveyQuestionType.Rating:
                {
                    var values = answers.Where(a => a.RatingValue.HasValue).Select(a => a.RatingValue!.Value).ToList();
                    var min = question.RatingMin ?? (values.Count > 0 ? values.Min() : 0);
                    var max = question.RatingMax ?? (values.Count > 0 ? values.Max() : min);
                    var distribution = new List<RatingBucket>();
                    for (long v = min; v <= max; v++)
                    {
                        distribution.Add(new RatingBucket((int)v, values.Count(rv => rv == v)));
                    }

                    double? average = values.Count > 0 ? values.Average() : null;
                    return new QuestionAggregate(question.Id, prompt, question.Type, [], distribution, average, []);
                }

            case SurveyQuestionType.Grid:
                {
                    var columns = question.Options
                        .OrderBy(option => option.Order)
                        .Select(option => new SurveyExportOption(
                            option.Value,
                            option.Label.Resolve(culture, culture)))
                        .ToList();
                    var rows = (question.GridRows ?? [])
                        .Select(row =>
                        {
                            var answeredCount = answers.Count(answer =>
                                answer.GridSelections?.TryGetValue(row.Value, out var selected) == true
                                && selected.Count > 0);
                            var cells = columns.Select(column =>
                            {
                                var count = answers.Count(answer =>
                                    answer.GridSelections?.TryGetValue(row.Value, out var selected) == true
                                    && selected.Contains(column.Value, StringComparer.Ordinal));
                                var percent = answeredCount == 0
                                    ? 0d
                                    : (double)count / answeredCount * 100d;
                                return new GridCellCount(column.Value, column.Label, count, percent);
                            }).ToList();
                            return new GridAggregateRow(
                                row.Value,
                                row.Label.Resolve(culture, culture),
                                cells);
                        })
                        .ToList();
                    var grid = new GridAggregate(
                        question.GridSelectionMode ?? GridSelectionMode.Single,
                        columns,
                        rows);
                    return new QuestionAggregate(question.Id, prompt, question.Type, [], [], null, [], grid);
                }

            case SurveyQuestionType.ShortText:
            case SurveyQuestionType.LongText:
            default:
                {
                    var texts = answers
                        .Where(a => !string.IsNullOrEmpty(a.TextValue))
                        .Select(a => a.TextValue!)
                        .ToList();
                    return new QuestionAggregate(question.Id, prompt, question.Type, [], [], null, texts);
                }
        }
    }

    internal static RankedQuestionResult BuildRankedQuestionResult(
        SurveyQuestion question,
        IReadOnlyList<SurveyResponse> responses,
        string culture)
    {
        var options = question.Options.OrderBy(option => option.Order).ToList();
        var authored = options.Select(option => option.Value).ToList();
        var unavailable = (question.RankedUnavailableOptionValues ?? [])
            .Where(value => authored.Contains(value, StringComparer.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var active = authored.Where(value => !unavailable.Contains(value)).ToHashSet(StringComparer.Ordinal);
        var labels = options.ToDictionary(
            option => option.Value,
            option => option.Label.Resolve(culture, culture),
            StringComparer.Ordinal);
        var ballots = responses
            .SelectMany(response => response.Answers)
            .Where(answer => answer.QuestionId == question.Id && answer.RankedValue is not null)
            .Select(answer => new RankedBallot(
                answer.RankedValue!.RankGroups,
                answer.RankedValue.Rejected.ToHashSet(StringComparer.Ordinal)))
            .ToList();
        RankedMethodResult Method(string key, string? winner, bool tieBreak) => new(
            key,
            winner,
            winner is null ? null : labels.GetValueOrDefault(winner, winner),
            tieBreak);

        (
            RankedPairwiseMatrix Matrix,
            RankedMethodResult Official,
            IReadOnlyList<RankedMethodResult> Methods,
            IReadOnlyList<string> PreferenceCycle)
            Count(IReadOnlySet<string>? candidates)
        {
            var matrix = RankedChoiceCounter.BuildPairwise(authored, ballots, candidates);
            var rankedPairs = RankedChoiceCounter.CountRankedPairs(authored, matrix, candidates);
            var condorcet = RankedChoiceCounter.CheckCondorcet(authored, matrix, candidates);
            var borda = RankedChoiceCounter.CountBorda(authored, ballots, candidates);
            var official = Method("Surveys_Results_MethodRankedPairsOfficial", rankedPairs.Winner, rankedPairs.TieBreakUsed);
            return (
                matrix,
                official,
                [
                    official,
                    Method("Surveys_Results_MethodCondorcetCheck", condorcet.Winner, false),
                    Method("Surveys_Results_MethodBordaCount", borda.Winner, borda.TieBreakUsed),
                ],
                condorcet.SmallestCycle);
        }

        var original = Count(null);
        var current = Count(active);

        return new RankedQuestionResult(
            options.Select(option => new RankedCandidateResult(
                option.Value,
                labels[option.Value],
                !unavailable.Contains(option.Value),
                ballots.Count(ballot => ballot.Rejected.Contains(option.Value)),
                ballots.Count == 0
                    ? 0d
                    : 100d * ballots.Count(ballot => ballot.Rejected.Contains(option.Value)) / ballots.Count))
                .ToList(),
            original.Official,
            current.Official,
            current.Methods,
            current.Matrix.Contests,
            original.PreferenceCycle,
            current.PreferenceCycle,
            unavailable.ToList());
    }
}
