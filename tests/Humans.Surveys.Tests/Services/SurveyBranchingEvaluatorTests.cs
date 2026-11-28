using AwesomeAssertions;
using Humans.Surveys.Services;
using Humans.Surveys.Domain;
namespace Humans.Surveys.Tests.Services;

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
    public void IsVisible_NullCondition_IsTrue()
        => SurveyBranchingEvaluator.IsVisible(null, NoAnswers()).Should().BeTrue();

    [HumansFact]
    public void IsVisible_EmptyClauses_IsTrue()
        => SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All), NoAnswers()).Should().BeTrue();

    [HumansFact]
    public void IsVisible_IsSelectedValue_IsTrue()
    {
        var q = Guid.NewGuid();
        var answers = Answers(q, "yes");
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.Is, "yes")), answers).Should().BeTrue();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.Is, "no")), answers).Should().BeFalse();
    }

    [HumansFact]
    public void IsVisible_IsOnUnanswered_IsFalse()
    {
        var q = Guid.NewGuid();
        SurveyBranchingEvaluator.IsVisible(
            Cond(BranchCombine.All, Clause(q, BranchOperator.Is, "yes")),
            NoAnswers()).Should().BeFalse();
    }

    [HumansFact]
    public void IsVisible_IsNotValueSelected_IsFalse()
    {
        var q = Guid.NewGuid();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.IsNot, "yes")), Answers(q, "no")).Should().BeTrue();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.IsNot, "yes")), Answers(q, "yes")).Should().BeFalse();
        // unanswered: the value is not selected → IsNot true
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.IsNot, "yes")),
            NoAnswers()).Should().BeTrue();
    }

    [HumansFact]
    public void IsVisible_AnsweredOperators_FollowAnswerState()
    {
        var q = Guid.NewGuid();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.Answered)), Answers(q, "a")).Should().BeTrue();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.Answered)), NoAnswers()).Should().BeFalse();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.NotAnswered)), NoAnswers()).Should().BeTrue();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.NotAnswered)), Answers(q, "a")).Should().BeFalse();
    }

    [HumansFact]
    public void IsVisible_AnsweredText_IsTrue()
    {
        var q = Guid.NewGuid();
        var answers = new Dictionary<Guid, AnswerState> { [q] = new AnswerState([], "some text", null) };
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.Answered)), answers).Should().BeTrue();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.NotAnswered)), answers).Should().BeFalse();
    }

    [HumansFact]
    public void IsVisible_AnsweredRating_IsTrue()
    {
        var q = Guid.NewGuid();
        var answers = new Dictionary<Guid, AnswerState> { [q] = new AnswerState([], null, 4) };
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.Answered)), answers).Should().BeTrue();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.NotAnswered)), answers).Should().BeFalse();
    }

    [HumansFact]
    public void IsVisible_WhitespaceText_IsNotAnswered()
    {
        var q = Guid.NewGuid();
        var answers = new Dictionary<Guid, AnswerState> { [q] = new AnswerState([], "   ", null) };
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.Answered)), answers).Should().BeFalse();
    }

    [HumansFact]
    public void IsVisible_AllCombine_NeedsEveryClause()
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
    public void IsVisible_AnyCombine_NeedsOneClause()
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
    public void IsVisible_IsOnMultiChoice_MatchesAnySelected()
    {
        var q = Guid.NewGuid();
        var answers = Answers(q, "a", "b");
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.Is, "b")), answers).Should().BeTrue();
    }

    [HumansFact]
    public void ValidateNoForwardReferences_SelfOrLater_IsFlagged()
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
    public void ValidateNoForwardReferences_AllBackward_Passes()
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
    public void ValidateClauseOptionValues_IsWithoutValues_IsFlagged()
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
    public void EffectiveAnswerStates_HiddenBranches_DropStaleAnswers()
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
