using AwesomeAssertions;
using Humans.Application.Services.Surveys;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
namespace Humans.Application.Tests.Surveys;

public class SurveyBranchingEvaluatorTests
{
    private static BranchCondition Cond(BranchCombine c, params BranchClause[] cl)
        => new() { Combine = c, Clauses = cl.ToList() };

    private static BranchClause Clause(Guid q, BranchOperator op, params string[] vals)
        => new() { QuestionId = q, Operator = op, OptionValues = vals.ToList() };

    private static Dictionary<Guid, AnswerState> NoAnswers() => new();

    private static Dictionary<Guid, AnswerState> Answers(Guid q, params string[] vals)
        => new() { [q] = new AnswerState(vals, null, null) };

    [HumansFact]
    public void Null_condition_is_visible()
        => SurveyBranchingEvaluator.IsVisible(null, NoAnswers()).Should().BeTrue();

    [HumansFact]
    public void Empty_clauses_is_visible()
        => SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All), NoAnswers()).Should().BeTrue();

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
            NoAnswers()).Should().BeFalse();
    }

    [HumansFact]
    public void IsNot_is_true_unless_value_selected()
    {
        var q = Guid.NewGuid();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.IsNot, "yes")), Answers(q, "no")).Should().BeTrue();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.IsNot, "yes")), Answers(q, "yes")).Should().BeFalse();
        // unanswered: the value is not selected → IsNot true
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.IsNot, "yes")),
            NoAnswers()).Should().BeTrue();
    }

    [HumansFact]
    public void Answered_and_NotAnswered()
    {
        var q = Guid.NewGuid();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.Answered)), Answers(q, "a")).Should().BeTrue();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.Answered)), NoAnswers()).Should().BeFalse();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.NotAnswered)), NoAnswers()).Should().BeTrue();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.NotAnswered)), Answers(q, "a")).Should().BeFalse();
    }

    [HumansFact]
    public void Answered_sees_text_answers()
    {
        var q = Guid.NewGuid();
        var answers = new Dictionary<Guid, AnswerState> { [q] = new AnswerState([], "some text", null) };
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.Answered)), answers).Should().BeTrue();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.NotAnswered)), answers).Should().BeFalse();
    }

    [HumansFact]
    public void Answered_sees_rating_answers()
    {
        var q = Guid.NewGuid();
        var answers = new Dictionary<Guid, AnswerState> { [q] = new AnswerState([], null, 4) };
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.Answered)), answers).Should().BeTrue();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.NotAnswered)), answers).Should().BeFalse();
    }

    [HumansFact]
    public void Whitespace_text_is_not_answered()
    {
        var q = Guid.NewGuid();
        var answers = new Dictionary<Guid, AnswerState> { [q] = new AnswerState([], "   ", null) };
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.Answered)), answers).Should().BeFalse();
    }

    [HumansFact]
    public void All_combine_is_and()
    {
        var q1 = Guid.NewGuid();
        var q2 = Guid.NewGuid();
        var answers = new Dictionary<Guid, AnswerState>
        {
            [q1] = new AnswerState(["a"], null, null),
            [q2] = new AnswerState(["b"], null, null),
        };
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
        var answers = Answers(q, "a", "b");
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

    [HumansFact]
    public void ValidateClauseOptionValues_flags_Is_and_IsNot_without_values()
    {
        var q1 = Guid.NewGuid();
        var q2 = Guid.NewGuid();
        var q3 = Guid.NewGuid();
        var q4 = Guid.NewGuid();
        var questions = new List<SurveyQuestion>
        {
            new() { Id = q1, PageNumber = 1, Order = 1 },
            // Is with no values → offender (could never match)
            new() { Id = q2, PageNumber = 1, Order = 2, ShowIf = Cond(BranchCombine.All, Clause(q1, BranchOperator.Is)) },
            // IsNot with no values → offender (always vacuously true)
            new() { Id = q3, PageNumber = 1, Order = 3, ShowIf = Cond(BranchCombine.All, Clause(q1, BranchOperator.IsNot)) },
            // Answered needs no values → ok
            new() { Id = q4, PageNumber = 1, Order = 4, ShowIf = Cond(BranchCombine.All, Clause(q1, BranchOperator.Answered)) },
        };

        SurveyBranchingEvaluator.ValidateClauseOptionValues(questions).Should().BeEquivalentTo(new[] { q2, q3 });
    }

    [HumansFact]
    public void EffectiveAnswerStates_drops_stale_answers_on_hidden_branches_transitively()
    {
        // Q1 gates Q2, Q2 gates Q3. Q2/Q3 were answered while Q1 == "yes"; after flipping Q1 to
        // "no", their stale answers must not count — directly or via each other.
        var q1 = Guid.NewGuid();
        var q2 = Guid.NewGuid();
        var q3 = Guid.NewGuid();
        var ordered = new[]
        {
            (q1, null),
            (q2, Cond(BranchCombine.All, Clause(q1, BranchOperator.Is, "yes"))),
            (q3, Cond(BranchCombine.All, Clause(q2, BranchOperator.Is, "vegetarian"))),
        };
        var raw = new Dictionary<Guid, AnswerState>
        {
            [q1] = new AnswerState(["no"], null, null),
            [q2] = new AnswerState(["vegetarian"], null, null),
            [q3] = new AnswerState([], "stale", null),
        };

        var effective = SurveyBranchingEvaluator.EffectiveAnswerStates(ordered, raw);

        effective.Keys.Should().BeEquivalentTo(new[] { q1 });
    }
}
