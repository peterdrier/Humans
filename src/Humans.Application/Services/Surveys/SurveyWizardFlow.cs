using Humans.Application.Interfaces.Surveys;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Services.Surveys;

/// <summary>
/// One question's captured answer in the wizard, carrying enough to derive both branch visibility
/// (from <paramref name="Options"/>) and answered-ness (any of options/text/rating present).
/// </summary>
public sealed record AnswerState(IReadOnlyList<string> Options, string? Text, int? Rating)
{
    /// <summary>An empty/unanswered state — no option, text, or rating.</summary>
    public static AnswerState None { get; } = new([], null, null);

    /// <summary>True when at least one of option/text/rating is present.</summary>
    public bool IsAnswered =>
        Options.Any(s => !string.IsNullOrEmpty(s)) || !string.IsNullOrWhiteSpace(Text) || Rating is not null;
}

/// <summary>
/// Pure, stateless page-navigation logic over the ordered question graph. Sits on top of
/// <see cref="SurveyBranchingEvaluator"/>: a page is reachable only when at least one of its
/// questions is visible under the answers gathered so far. Operates on builder DTOs
/// (<see cref="QuestionInput"/>) so the controller passes them straight through.
/// </summary>
public static class SurveyWizardFlow
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
    /// in the order given. A question is answered when it has any selected option, text, or rating.
    /// </summary>
    public static IReadOnlyList<Guid> RequiredUnanswered(
        IReadOnlyList<QuestionInput> visibleQuestions, IReadOnlyDictionary<Guid, AnswerState> answers)
        => visibleQuestions
            .Where(q => q.Id is { } id && q.IsRequired && !(answers.TryGetValue(id, out var a) && a.IsAnswered))
            .Select(q => q.Id!.Value)
            .ToList();

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
                result[id] = new AnswerState(a.SelectedOptionValues, a.TextValue, a.RatingValue);
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
                kv.Value.RatingValue))
            .ToList();
}
