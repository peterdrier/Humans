using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;

namespace Humans.Application.Services.Surveys;

/// <summary>
/// Pure, stateless branching logic. Reused by the answering wizard (page/question visibility,
/// required-validation, rejecting answers to hidden questions) and by author-save validation
/// (no-forward-reference check). Answers-so-far are modelled as questionId → selected option
/// values; an empty or absent entry means unanswered.
/// </summary>
public static class SurveyBranchingEvaluator
{
    /// <summary>True when <paramref name="cond"/> is null/empty or its clauses combine (All/Any) to true against <paramref name="answers"/>.</summary>
    public static bool IsVisible(BranchCondition? cond, IReadOnlyDictionary<Guid, IReadOnlyList<string>> answers)
    {
        if (cond is null || cond.Clauses.Count == 0) return true;

        return cond.Combine == BranchCombine.All
            ? cond.Clauses.All(c => Matches(c, answers))
            : cond.Clauses.Any(c => Matches(c, answers));
    }

    private static bool Matches(BranchClause clause, IReadOnlyDictionary<Guid, IReadOnlyList<string>> answers)
    {
        var selected = answers.TryGetValue(clause.QuestionId, out var v) && v is not null ? v : [];

        return clause.Operator switch
        {
            BranchOperator.Is => selected.Intersect(clause.OptionValues, StringComparer.Ordinal).Any(),
            BranchOperator.IsNot => !selected.Intersect(clause.OptionValues, StringComparer.Ordinal).Any(),
            BranchOperator.Answered => selected.Any(s => !string.IsNullOrEmpty(s)),
            BranchOperator.NotAnswered => !selected.Any(s => !string.IsNullOrEmpty(s)),
            _ => false
        };
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
}
