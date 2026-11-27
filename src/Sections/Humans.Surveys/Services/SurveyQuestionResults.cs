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
                    var min = question.RatingMin ?? 1;
                    var max = question.RatingMax ?? 5;
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

    /// <summary>
    /// Links a question to the earlier choice question its show-if names, when that show-if is a single
    /// "is" clause, and groups a free-text follow-up's answers by each respondent's choice there.
    /// </summary>
    internal static QuestionAggregate WithFollowUp(
        QuestionAggregate aggregate,
        SurveyQuestion question,
        IEnumerable<SurveyQuestion> allQuestions,
        IReadOnlyList<SurveyResponse> responses,
        string culture)
    {
        if (question.ShowIf is not { Clauses: [{ Operator: BranchOperator.Is } clause] }) return aggregate;

        var parent = allQuestions.FirstOrDefault(q => q.Id == clause.QuestionId);
        if (parent is null
            || parent.Type is not (SurveyQuestionType.SingleChoice or SurveyQuestionType.MultiChoice)
            || (parent.PageNumber, parent.Order).CompareTo((question.PageNumber, question.Order)) >= 0)
            return aggregate;

        var parentOptions = parent.Options.OrderBy(o => o.Order).ToList();
        var groups = question.Type is SurveyQuestionType.ShortText or SurveyQuestionType.LongText
            ? responses
                .Select(r => (
                    Chosen: parentOptions
                        .Where(o => r.Answers.Any(a => a.QuestionId == parent.Id
                            && a.SelectedOptionValues.Contains(o.Value, StringComparer.Ordinal)))
                        .ToList(),
                    Text: r.Answers.FirstOrDefault(a => a.QuestionId == question.Id)?.TextValue))
                .Where(x => !string.IsNullOrEmpty(x.Text))
                .GroupBy(x => string.Join(", ", x.Chosen.Select(o => o.Label.Resolve(culture, culture))), StringComparer.Ordinal)
                .OrderBy(g => g.First().Chosen.Select(o => parentOptions.IndexOf(o)).DefaultIfEmpty(int.MaxValue).Min())
                .Select(g => new FreeTextGroup(g.Key, g.Select(x => x.Text!).ToList()))
                .ToList()
            : [];

        return aggregate with
        {
            FollowUpOf = parent.Id,
            AskedWhen = parentOptions
                .Where(o => clause.OptionValues.Contains(o.Value, StringComparer.Ordinal))
                .Select(o => o.Label.Resolve(culture, culture))
                .ToList(),
            FreeTextByParentChoice = groups,
        };
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
            current.PreferenceCycle);
    }
}
