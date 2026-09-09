using Humans.Surveys.Contracts;
using Humans.Surveys.Domain;
using NodaTime;

namespace Humans.Surveys.Services;

/// <summary>
/// One question's captured answer in the wizard, carrying enough to derive both branch visibility
/// (from <paramref name="Options"/>) and answered-ness across every supported answer shape.
/// </summary>
internal sealed record AnswerState(
    IReadOnlyList<string> Options,
    string? Text,
    int? Rating,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Grid = null,
    RankedAnswer? Ranked = null)
{
    /// <summary>An empty/unanswered state.</summary>
    public static AnswerState None { get; } = new([], null, null);

    /// <summary>True when at least one option, text, rating, or Grid cell is present.</summary>
    public bool IsAnswered =>
        Options.Any(s => !string.IsNullOrEmpty(s))
        || !string.IsNullOrWhiteSpace(Text)
        || Rating is not null
        || Grid?.Values.Any(values => values.Count > 0) == true
        || Ranked is { } ranked && (ranked.RankGroups.Any(group => group.Count > 0) || ranked.Rejected.Count > 0);
}

/// <summary>
/// Pure, stateless page-navigation logic over the ordered question graph. Sits on top of
/// <see cref="SurveyBranchingEvaluator"/>: a page is reachable only when at least one of its
/// questions is visible under the answers gathered so far. Operates on builder DTOs
/// (<see cref="QuestionInput"/>) so the controller passes them straight through.
/// </summary>
internal static class SurveyWizardFlow
{
    /// <summary>Distinct page numbers present in the graph, ascending.</summary>
    public static IReadOnlyList<int> OrderedPages(IReadOnlyList<QuestionInput> questions)
        => questions.Select(q => q.PageNumber).Distinct().OrderBy(p => p).ToList();

    /// <summary>
    /// Questions on <paramref name="page"/> whose <c>ShowIf</c> is satisfied by <paramref name="answers"/>,
    /// in display order. Visibility sees the full answer state (options, text, rating) and cascades:
    /// answers to questions that are themselves hidden are ignored, so a stale answer on a hidden
    /// branch cannot keep downstream questions visible.
    /// </summary>
    public static IReadOnlyList<QuestionInput> VisibleQuestionsOnPage(
        IReadOnlyList<QuestionInput> questions, int page, IReadOnlyDictionary<Guid, AnswerState> answers)
    {
        var effective = SurveyBranchingEvaluator.EffectiveAnswerStates(
            questions
                .Where(q => q.Id is not null)
                .OrderBy(q => q.PageNumber).ThenBy(q => q.Order)
                .Select(q => (q.Id!.Value, q.ShowIf)),
            answers);

        return questions
            .Where(q => q.PageNumber == page && SurveyBranchingEvaluator.IsVisible(q.ShowIf, effective))
            .OrderBy(q => q.Order)
            .ToList();
    }

    /// <summary>The first page (ascending) that has at least one visible question, or null if none.</summary>
    public static int? FirstVisiblePage(
        IReadOnlyList<QuestionInput> questions, IReadOnlyDictionary<Guid, AnswerState> answers)
        => OrderedPages(questions).Cast<int?>()
            .FirstOrDefault(p => VisibleQuestionsOnPage(questions, p!.Value, answers).Count > 0);

    /// <summary>
    /// The next page after <paramref name="currentPage"/> that has at least one visible question, or
    /// null when none remain (⇒ the wizard is ready to submit).
    /// </summary>
    public static int? NextVisiblePage(
        IReadOnlyList<QuestionInput> questions, int currentPage, IReadOnlyDictionary<Guid, AnswerState> answers)
        => OrderedPages(questions)
            .Where(p => p > currentPage)
            .Cast<int?>()
            .FirstOrDefault(p => VisibleQuestionsOnPage(questions, p!.Value, answers).Count > 0);

    /// <summary>
    /// Ids of the supplied (already visibility-filtered) questions that are required but unanswered,
    /// in the order given. Grid questions additionally require a valid answer for every row.
    /// </summary>
    public static IReadOnlyList<Guid> RequiredUnanswered(
        IReadOnlyList<QuestionInput> visibleQuestions, IReadOnlyDictionary<Guid, AnswerState> answers)
        => visibleQuestions
            .Where(q => q.Type != SurveyQuestionType.Information
                && q.Id is { } id && q.IsRequired
                && !(answers.TryGetValue(id, out var a) && IsAnswered(q, a)))
            .Select(q => q.Id!.Value)
            .ToList();

    private static bool IsAnswered(QuestionInput question, AnswerState answer)
    {
        if (question.Type != SurveyQuestionType.Grid) return answer.IsAnswered;

        var rows = question.GridRows ?? [];
        if (rows.Count == 0 || answer.Grid is null) return false;
        return rows.All(row =>
            answer.Grid.TryGetValue(row.Value, out var selected)
            && selected.Count > 0
            && (question.GridSelectionMode != GridSelectionMode.Single || selected.Count == 1));
    }

    /// <summary>The nearest page strictly before <paramref name="page"/> that has a visible question, or null at the start.</summary>
    public static int? PreviousVisiblePage(
        IReadOnlyList<QuestionInput> questions, int page, IReadOnlyDictionary<Guid, AnswerState> answers)
        => OrderedPages(questions)
            .Where(p => p < page && VisibleQuestionsOnPage(questions, p, answers).Count > 0)
            .Cast<int?>()
            .LastOrDefault();

    /// <summary>
    /// True when the survey can be answered at <paramref name="now"/>: status Open and within the
    /// optional [<c>opensAt</c>, <c>closesAt</c>] window. The single home for the answer-window rule —
    /// applied by the controller at every entry/page gate and by the service at submit.
    /// </summary>
    public static bool IsAnswerable(SurveyStatus status, Instant? opensAt, Instant? closesAt, Instant now)
    {
        if (status != SurveyStatus.Open) return false;
        if (opensAt is { } from && now < from) return false;
        if (closesAt is { } until && now > until) return false;
        return true;
    }

    /// <summary>Projects the session answers into the flow's <see cref="AnswerState"/> map (keyed by question id).</summary>
    public static Dictionary<Guid, AnswerState> ToAnswerStates(IReadOnlyDictionary<string, SurveyWizardAnswer> answers)
    {
        var result = new Dictionary<Guid, AnswerState>();
        foreach (var (key, a) in answers)
        {
            if (Guid.TryParse(key, out var id))
            {
                result[id] = new AnswerState(
                    a.SelectedOptionValues,
                    a.TextValue,
                    a.RatingValue,
                    a.GridSelections.ToDictionary(
                        kv => kv.Key,
                        kv => (IReadOnlyList<string>)kv.Value,
                        StringComparer.Ordinal),
                    a.RankedValue);
            }
        }

        return result;
    }

    /// <summary>Maps the session answers to the submission/autosave shape.</summary>
    public static IReadOnlyList<SurveyAnswerInput> ToAnswerInputs(IReadOnlyDictionary<string, SurveyWizardAnswer> answers)
        => answers
            .Where(kv => Guid.TryParse(kv.Key, out _))
            .Select(kv => new SurveyAnswerInput(
                Guid.Parse(kv.Key),
                kv.Value.SelectedOptionValues,
                kv.Value.TextValue,
                kv.Value.RatingValue,
                kv.Value.GridSelections.Count == 0
                    ? null
                    : kv.Value.GridSelections.ToDictionary(
                        pair => pair.Key,
                        pair => (IReadOnlyList<string>)pair.Value,
                        StringComparer.Ordinal),
                kv.Value.RankedValue))
            .ToList();
}
