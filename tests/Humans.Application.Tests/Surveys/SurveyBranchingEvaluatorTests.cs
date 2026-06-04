using AwesomeAssertions;
using Humans.Application.Services.Surveys;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using Xunit;

namespace Humans.Application.Tests.Surveys;

public class SurveyBranchingEvaluatorTests
{
    private static BranchCondition Cond(BranchCombine c, params BranchClause[] cl)
        => new() { Combine = c, Clauses = cl.ToList() };

    private static BranchClause Clause(Guid q, BranchOperator op, params string[] vals)
        => new() { QuestionId = q, Operator = op, OptionValues = vals.ToList() };

    private static Dictionary<Guid, IReadOnlyList<string>> Answers(Guid q, params string[] vals)
        => new() { [q] = vals };

    [HumansFact]
    public void Null_condition_is_visible()
        => SurveyBranchingEvaluator.IsVisible(null, new Dictionary<Guid, IReadOnlyList<string>>()).Should().BeTrue();

    [HumansFact]
    public void Empty_clauses_is_visible()
        => SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All), new Dictionary<Guid, IReadOnlyList<string>>()).Should().BeTrue();

    [HumansFact]
    public void Is_matches_selected_value()
    {
        var q = Guid.NewGuid();
        var answers = Answers(q, "yes");
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.Is, "yes")), answers).Should().BeTrue();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.Is, "no")), answers).Should().BeFalse();
    }

    [HumansFact]
    public void Is_on_unanswered_question_is_false()
    {
        var q = Guid.NewGuid();
        SurveyBranchingEvaluator.IsVisible(
            Cond(BranchCombine.All, Clause(q, BranchOperator.Is, "yes")),
            new Dictionary<Guid, IReadOnlyList<string>>()).Should().BeFalse();
    }

    [HumansFact]
    public void IsNot_is_true_unless_value_selected()
    {
        var q = Guid.NewGuid();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.IsNot, "yes")), Answers(q, "no")).Should().BeTrue();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.IsNot, "yes")), Answers(q, "yes")).Should().BeFalse();
        // unanswered: the value is not selected → IsNot true
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.IsNot, "yes")),
            new Dictionary<Guid, IReadOnlyList<string>>()).Should().BeTrue();
    }

    [HumansFact]
    public void Answered_and_NotAnswered()
    {
        var q = Guid.NewGuid();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.Answered)), Answers(q, "a")).Should().BeTrue();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.Answered)), new Dictionary<Guid, IReadOnlyList<string>>()).Should().BeFalse();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.NotAnswered)), new Dictionary<Guid, IReadOnlyList<string>>()).Should().BeTrue();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.NotAnswered)), Answers(q, "a")).Should().BeFalse();
    }

    [HumansFact]
    public void All_combine_is_and()
    {
        var q1 = Guid.NewGuid();
        var q2 = Guid.NewGuid();
        var answers = new Dictionary<Guid, IReadOnlyList<string>> { [q1] = new[] { "a" }, [q2] = new[] { "b" } };
        SurveyBranchingEvaluator.IsVisible(
            Cond(BranchCombine.All, Clause(q1, BranchOperator.Is, "a"), Clause(q2, BranchOperator.Is, "b")), answers).Should().BeTrue();
        SurveyBranchingEvaluator.IsVisible(
            Cond(BranchCombine.All, Clause(q1, BranchOperator.Is, "a"), Clause(q2, BranchOperator.Is, "z")), answers).Should().BeFalse();
    }

    [HumansFact]
    public void Any_combine_is_or()
    {
        var q = Guid.NewGuid();
        var answers = Answers(q, "a");
        SurveyBranchingEvaluator.IsVisible(
            Cond(BranchCombine.Any, Clause(q, BranchOperator.Is, "a"), Clause(q, BranchOperator.Is, "z")), answers)
            .Should().BeTrue();
        SurveyBranchingEvaluator.IsVisible(
            Cond(BranchCombine.Any, Clause(q, BranchOperator.Is, "y"), Clause(q, BranchOperator.Is, "z")), answers)
            .Should().BeFalse();
    }

    [HumansFact]
    public void Is_on_multichoice_matches_any_selected()
    {
        var q = Guid.NewGuid();
        var answers = new Dictionary<Guid, IReadOnlyList<string>> { [q] = new[] { "a", "b" } };
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.Is, "b")), answers).Should().BeTrue();
    }

    [HumansFact]
    public void ValidateNoForwardReferences_flags_self_and_later_references()
    {
        var q1 = Guid.NewGuid();
        var q2 = Guid.NewGuid();
        var q3 = Guid.NewGuid();
        var questions = new List<SurveyQuestion>
        {
            new() { Id = q1, PageNumber = 1, Order = 1 },
            // q2 references q3 (later) → offender
            new() { Id = q2, PageNumber = 1, Order = 2, ShowIf = Cond(BranchCombine.All, Clause(q3, BranchOperator.Is, "x")) },
            // q3 references q1 (earlier) → ok
            new() { Id = q3, PageNumber = 2, Order = 1, ShowIf = Cond(BranchCombine.All, Clause(q1, BranchOperator.Is, "x")) },
        };

        SurveyBranchingEvaluator.ValidateNoForwardReferences(questions).Should().BeEquivalentTo(new[] { q2 });
    }

    [HumansFact]
    public void ValidateNoForwardReferences_passes_when_all_backward()
    {
        var q1 = Guid.NewGuid();
        var q2 = Guid.NewGuid();
        var questions = new List<SurveyQuestion>
        {
            new() { Id = q1, PageNumber = 1, Order = 1 },
            new() { Id = q2, PageNumber = 1, Order = 2, ShowIf = Cond(BranchCombine.All, Clause(q1, BranchOperator.Is, "x")) },
        };

        SurveyBranchingEvaluator.ValidateNoForwardReferences(questions).Should().BeEmpty();
    }
}
