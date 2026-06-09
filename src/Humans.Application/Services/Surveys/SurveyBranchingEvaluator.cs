using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;

namespace Humans.Application.Services.Surveys;

/// <summary>
/// Pure, stateless branching logic. Reused by the answering wizard (page/question visibility,
/// required-validation, rejecting answers to hidden questions) and by author-save validation
/// (no-forward-reference check). Answers-so-far are modelled as questionId → <see cref="AnswerState"/>;
/// an absent entry means unanswered. <c>Is</c>/<c>IsNot</c> match selected option values;
/// <c>Answered</c>/<c>NotAnswered</c> consider options, text, and rating alike.
/// </summary>
public static class SurveyBranchingEvaluator
{
    /// <summary>True when <paramref name="cond"/> is null/empty or its clauses combine (All/Any) to true against <paramref name="answers"/>.</summary>
    public static bool IsVisible(BranchCondition? cond, IReadOnlyDictionary<Guid, AnswerState> answers)
    {
        if (cond is null || cond.Clauses.Count == 0) return true;

        return cond.Combine == BranchCombine.All
            ? cond.Clauses.All(c => Matches(c, answers))
            : cond.Clauses.Any(c => Matches(c, answers));
    }

    private static bool Matches(BranchClause clause, IReadOnlyDictionary<Guid, AnswerState> answers)
    {
        var state = answers.GetValueOrDefault(clause.QuestionId) ?? AnswerState.None;

        return clause.Operator switch
        {
            BranchOperator.Is => state.Options.Intersect(clause.OptionValues, StringComparer.Ordinal).Any(),
            BranchOperator.IsNot => !state.Options.Intersect(clause.OptionValues, StringComparer.Ordinal).Any(),
            BranchOperator.Answered => state.IsAnswered,
            BranchOperator.NotAnswered => !state.IsAnswered,
            _ => false
        };
    }

    /// <summary>
    /// Reduces raw answers to the set effective under cascading visibility. Questions are walked in
    /// authoring order (<c>ShowIf</c> may only reference strictly earlier questions, enforced at save),
    /// and an answer only counts when its own question is visible under the answers accepted so far.
    /// This drops stale answers left behind when an earlier change hid their question, so they can no
    /// longer satisfy (or block) downstream conditions.
    /// </summary>
    public static Dictionary<Guid, AnswerState> EffectiveAnswerStates(
        IEnumerable<(Guid Id, BranchCondition? ShowIf)> orderedQuestions,
        IReadOnlyDictionary<Guid, AnswerState> answers)
    {
        var effective = new Dictionary<Guid, AnswerState>();
        foreach (var (id, showIf) in orderedQuestions)
        {
            if (IsVisible(showIf, effective) && answers.TryGetValue(id, out var answer))
            {
                effective[id] = answer;
            }
        }

        return effective;
    }

    /// <summary>
    /// Returns the ids of questions whose <c>ShowIf</c> references a question that is not strictly
    /// earlier in <c>(PageNumber, Order)</c> (a self- or forward-reference, or a reference to a
    /// question not in the set). Empty list = valid.
    /// </summary>
    public static IReadOnlyList<Guid> ValidateNoForwardReferences(IReadOnlyList<SurveyQuestion> ordered)
    {
        var offenders = new List<Guid>();
        var seenEarlier = new HashSet<Guid>();

        foreach (var q in ordered.OrderBy(x => x.PageNumber).ThenBy(x => x.Order))
        {
            if (q.ShowIf is not null &&
                q.ShowIf.Clauses.Any(clause => !seenEarlier.Contains(clause.QuestionId)))
            {
                offenders.Add(q.Id);
            }

            seenEarlier.Add(q.Id);
        }

        return offenders;
    }

    /// <summary>
    /// Returns the ids of questions with a malformed <c>ShowIf</c> clause: an <c>Is</c>/<c>IsNot</c>
    /// operator with no option values. Such a clause is vacuous (<c>IsNot</c> would always match,
    /// <c>Is</c> never), so authoring rejects it. Empty list = valid.
    /// </summary>
    public static IReadOnlyList<Guid> ValidateClauseOptionValues(IReadOnlyList<SurveyQuestion> questions)
        => questions
            .Where(q => q.ShowIf is not null && q.ShowIf.Clauses.Any(clause =>
                clause.Operator is BranchOperator.Is or BranchOperator.IsNot
                && clause.OptionValues.Count == 0))
            .Select(q => q.Id)
            .ToList();
}
