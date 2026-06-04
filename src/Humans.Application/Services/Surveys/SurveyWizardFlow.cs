using Humans.Application.Interfaces.Surveys;

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
    /// in display order. Visibility uses each answer's selected option values.
    /// </summary>
    public static IReadOnlyList<QuestionInput> VisibleQuestionsOnPage(
        IReadOnlyList<QuestionInput> questions, int page, IReadOnlyDictionary<Guid, AnswerState> answers)
    {
        var visibility = ToVisibility(answers);
        return questions
            .Where(q => q.PageNumber == page && SurveyBranchingEvaluator.IsVisible(q.ShowIf, visibility))
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

    private static IReadOnlyDictionary<Guid, IReadOnlyList<string>> ToVisibility(
        IReadOnlyDictionary<Guid, AnswerState> answers)
        => answers.ToDictionary(kv => kv.Key, kv => kv.Value.Options);
}
