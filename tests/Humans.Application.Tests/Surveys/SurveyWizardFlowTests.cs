using AwesomeAssertions;
using Humans.Application.Interfaces.Surveys;
using Humans.Application.Services.Surveys;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using Xunit;

namespace Humans.Application.Tests.Surveys;

public class SurveyWizardFlowTests
{
    private static LocalizedText L(string en) => new(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = en });

    private static OptionInput Opt(string value) => new(Guid.NewGuid(), 0, value, L(value));

    private static QuestionInput Q(
        Guid id, int page, int order, SurveyQuestionType type,
        bool required = false, BranchCondition? showIf = null, params OptionInput[] opts) =>
        new(id, page, order, type, L("Q"), LocalizedText.Empty, required, null, null,
            LocalizedText.Empty, LocalizedText.Empty, showIf, opts.ToList());

    private static BranchCondition ShowIfIs(Guid q, string value) => new()
    {
        Combine = BranchCombine.All,
        Clauses = { new BranchClause { QuestionId = q, Operator = BranchOperator.Is, OptionValues = { value } } },
    };

    private static Dictionary<Guid, AnswerState> Chose(Guid q, params string[] values) =>
        new() { [q] = new AnswerState(values, null, null) };

    [HumansFact]
    public void OrderedPages_returns_distinct_ascending_pages()
    {
        var questions = new[]
        {
            Q(Guid.NewGuid(), 3, 1, SurveyQuestionType.ShortText),
            Q(Guid.NewGuid(), 1, 1, SurveyQuestionType.ShortText),
            Q(Guid.NewGuid(), 1, 2, SurveyQuestionType.ShortText),
            Q(Guid.NewGuid(), 2, 1, SurveyQuestionType.ShortText),
        };

        SurveyWizardFlow.OrderedPages(questions).Should().ContainInOrder(1, 2, 3);
    }

    [HumansFact]
    public void VisibleQuestionsOnPage_filters_by_ShowIf_and_orders_by_Order()
    {
        var gate = Guid.NewGuid();
        var shown = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        var first = Guid.NewGuid();
        var questions = new[]
        {
            Q(gate, 1, 1, SurveyQuestionType.SingleChoice, opts: Opt("yes")),
            Q(shown, 2, 2, SurveyQuestionType.ShortText, showIf: ShowIfIs(gate, "yes")),
            Q(hidden, 2, 3, SurveyQuestionType.ShortText, showIf: ShowIfIs(gate, "no")),
            Q(first, 2, 1, SurveyQuestionType.ShortText),
        };

        var visible = SurveyWizardFlow.VisibleQuestionsOnPage(questions, 2, Chose(gate, "yes"));

        visible.Select(q => q.Id).Should().ContainInOrder(first, shown);
        visible.Select(q => q.Id).Should().NotContain(hidden);
    }

    [HumansFact]
    public void VisibleQuestionsOnPage_ignores_stale_answers_on_hidden_branches()
    {
        // Q1 gates Q2, Q2 gates Q3. Q2 was answered while Q1 == "yes"; the respondent went back
        // and flipped Q1 to "no" — Q2's stale answer must not keep Q3 visible.
        var q1 = Guid.NewGuid();
        var q2 = Guid.NewGuid();
        var q3 = Guid.NewGuid();
        var questions = new[]
        {
            Q(q1, 1, 1, SurveyQuestionType.SingleChoice, opts: Opt("yes")),
            Q(q2, 2, 1, SurveyQuestionType.SingleChoice, showIf: ShowIfIs(q1, "yes"), opts: Opt("vegetarian")),
            Q(q3, 3, 1, SurveyQuestionType.ShortText, showIf: ShowIfIs(q2, "vegetarian")),
        };
        var answers = new Dictionary<Guid, AnswerState>
        {
            [q1] = new AnswerState(["no"], null, null),
            [q2] = new AnswerState(["vegetarian"], null, null),
        };

        SurveyWizardFlow.VisibleQuestionsOnPage(questions, 2, answers).Should().BeEmpty();
        SurveyWizardFlow.VisibleQuestionsOnPage(questions, 3, answers).Should().BeEmpty();
    }

    [HumansFact]
    public void NextVisiblePage_skips_a_page_whose_questions_are_all_hidden()
    {
        var gate = Guid.NewGuid();
        var questions = new[]
        {
            Q(gate, 1, 1, SurveyQuestionType.SingleChoice, opts: Opt("yes")),
            // page 2: only visible when gate == "yes" (it is "no") → entire page hidden
            Q(Guid.NewGuid(), 2, 1, SurveyQuestionType.ShortText, showIf: ShowIfIs(gate, "yes")),
            // page 3: always visible
            Q(Guid.NewGuid(), 3, 1, SurveyQuestionType.ShortText),
        };

        SurveyWizardFlow.NextVisiblePage(questions, 1, Chose(gate, "no")).Should().Be(3);
    }

    [HumansFact]
    public void NextVisiblePage_returns_null_when_no_later_page_has_visible_questions()
    {
        var gate = Guid.NewGuid();
        var questions = new[]
        {
            Q(gate, 1, 1, SurveyQuestionType.SingleChoice, opts: Opt("yes")),
            Q(Guid.NewGuid(), 2, 1, SurveyQuestionType.ShortText, showIf: ShowIfIs(gate, "yes")),
        };

        SurveyWizardFlow.NextVisiblePage(questions, 1, Chose(gate, "no")).Should().BeNull();
    }

    [HumansFact]
    public void NextVisiblePage_reveals_a_later_page_when_a_branch_is_taken()
    {
        var gate = Guid.NewGuid();
        var questions = new[]
        {
            Q(gate, 1, 1, SurveyQuestionType.SingleChoice, opts: Opt("yes")),
            Q(Guid.NewGuid(), 2, 1, SurveyQuestionType.ShortText, showIf: ShowIfIs(gate, "yes")),
        };

        SurveyWizardFlow.NextVisiblePage(questions, 1, Chose(gate, "yes")).Should().Be(2);
    }

    [HumansFact]
    public void FirstVisiblePage_returns_the_first_page_with_a_visible_question()
    {
        var gate = Guid.NewGuid();
        var questions = new[]
        {
            // page 1 hidden under current answers
            Q(Guid.NewGuid(), 1, 1, SurveyQuestionType.ShortText, showIf: ShowIfIs(gate, "yes")),
            Q(gate, 2, 1, SurveyQuestionType.SingleChoice, opts: Opt("yes")),
        };

        SurveyWizardFlow.FirstVisiblePage(questions, new Dictionary<Guid, AnswerState>()).Should().Be(2);
    }

    [HumansFact]
    public void RequiredUnanswered_lists_only_visible_required_unanswered_questions()
    {
        var gate = Guid.NewGuid();
        var requiredText = Guid.NewGuid();
        var optionalText = Guid.NewGuid();
        var hiddenRequired = Guid.NewGuid();
        var visible = new[]
        {
            Q(requiredText, 1, 1, SurveyQuestionType.ShortText, required: true),
            Q(optionalText, 1, 2, SurveyQuestionType.ShortText),
        };

        var answers = new Dictionary<Guid, AnswerState>();
        var missing = SurveyWizardFlow.RequiredUnanswered(visible, answers);

        missing.Should().ContainInOrder(requiredText);
        missing.Should().NotContain(optionalText);
        missing.Should().NotContain(hiddenRequired);
        missing.Should().NotContain(gate);
    }

    [HumansFact]
    public void RequiredUnanswered_treats_any_of_options_text_rating_as_answered()
    {
        var choice = Guid.NewGuid();
        var text = Guid.NewGuid();
        var rating = Guid.NewGuid();
        var visible = new[]
        {
            Q(choice, 1, 1, SurveyQuestionType.SingleChoice, required: true, opts: Opt("a")),
            Q(text, 1, 2, SurveyQuestionType.ShortText, required: true),
            Q(rating, 1, 3, SurveyQuestionType.Rating, required: true),
        };

        var answers = new Dictionary<Guid, AnswerState>
        {
            [choice] = new AnswerState(["a"], null, null),
            [text] = new AnswerState([], "hi", null),
            [rating] = new AnswerState([], null, 5),
        };

        SurveyWizardFlow.RequiredUnanswered(visible, answers).Should().BeEmpty();
    }
}
