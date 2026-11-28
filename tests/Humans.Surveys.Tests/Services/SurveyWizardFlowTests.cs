using AwesomeAssertions;
using Humans.Surveys.Services;
using Humans.Surveys.Domain;
using Humans.Surveys.Contracts;

namespace Humans.Surveys.Tests.Services;

public class SurveyWizardFlowTests
{
    private static LocalizedText L(string en) => new(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = en });

    private static OptionInput Opt(string value) => new(Guid.NewGuid(), 0, value, L(value));

    private static QuestionInput Q(
        Guid id, int page, int order, SurveyQuestionType type,
        bool required = false, BranchCondition? showIf = null, params OptionInput[] opts) =>
        new(id, page, order, type, L("Q"), LocalizedText.Empty, required, null, null,
            LocalizedText.Empty, LocalizedText.Empty, showIf, opts.ToList());

    private static QuestionInput Grid(
        Guid id,
        GridSelectionMode mode,
        bool required = true) =>
        new(
            id, 1, 0, SurveyQuestionType.Grid, L("Availability"), LocalizedText.Empty,
            required, null, null, LocalizedText.Empty, LocalizedText.Empty, null,
            [Opt("morning"), Opt("afternoon")],
            mode,
            [new GridRowInput("monday", L("Monday")), new GridRowInput("tuesday", L("Tuesday"))]);

    private static BranchCondition ShowIfIs(Guid q, string value) => new()
    {
        Combine = BranchCombine.All,
        Clauses = { new BranchClause { QuestionId = q, Operator = BranchOperator.Is, OptionValues = { value } } },
    };

    private static Dictionary<Guid, AnswerState> Chose(Guid q, params string[] values) =>
        new() { [q] = new AnswerState(values, null, null) };

    [HumansTheory]
    [Xunit.InlineData(true, true, ResponseAnonymity.Anonymous, ResponseAnonymity.CompletionTracked)]
    [Xunit.InlineData(false, true, ResponseAnonymity.Anonymous, ResponseAnonymity.Anonymous)]
    [Xunit.InlineData(false, true, ResponseAnonymity.Identified, ResponseAnonymity.Identified)]
    [Xunit.InlineData(false, false, ResponseAnonymity.Anonymous, ResponseAnonymity.Identified)]
    public void InvitedAnonymity_Vote_OverridesAnonymousSetting(
        bool isAsociadoVote, bool allowAnonymous, ResponseAnonymity requested, ResponseAnonymity expected)
    {
        var editable = new SurveyEditInput(
            L("T"), LocalizedText.Empty, LocalizedText.Empty, LocalizedText.Empty, LocalizedText.Empty,
            "en", allowAnonymous, null, null, null, null, null, null, [], isAsociadoVote);

        SurveyWizardFlow.ResolveInvitedAnonymity(editable, requested).Should().Be(expected);
    }

    [HumansFact]
    public void PublicTrackedState_OtherUser_IsNotAccessible()
    {
        var ownerId = Guid.NewGuid();
        var state = new SurveyWizardState
        {
            Anonymity = ResponseAnonymity.Identified,
            UserId = ownerId,
        };

        state.IsPubliclyAccessibleBy(ownerId).Should().BeTrue();
        state.IsPubliclyAccessibleBy(Guid.NewGuid()).Should().BeFalse();
        state.IsPubliclyAccessibleBy(null).Should().BeFalse();
    }

    [HumansFact]
    public void PublicAnonymousState_NoPrincipal_IsAccessible()
    {
        var state = new SurveyWizardState
        {
            Anonymity = ResponseAnonymity.Anonymous,
            UserId = null,
        };

        state.IsPubliclyAccessibleBy(null).Should().BeTrue();
        state.IsPubliclyAccessibleBy(Guid.NewGuid()).Should().BeTrue();
    }

    [HumansFact]
    public void OrderedPages_Pages_AreDistinctAscending()
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
    public void VisibleQuestionsOnPage_ShowIf_FiltersThenOrders()
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
    public void VisibleQuestionsOnPage_HiddenBranchAnswers_AreIgnored()
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
    public void NextVisiblePage_AllQuestionsHidden_SkipsPage()
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
    public void NextVisiblePage_NoLaterVisiblePage_ReturnsNull()
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
    public void NextVisiblePage_BranchTaken_RevealsLaterPage()
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
    public void FirstVisiblePage_Pages_ReturnsFirstVisible()
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
    public void RequiredUnanswered_MixedQuestions_ListsVisibleRequired()
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

        IReadOnlyDictionary<Guid, AnswerState> answers = new Dictionary<Guid, AnswerState>();
        var missing = SurveyWizardFlow.RequiredUnanswered(visible, answers);

        missing.Should().ContainInOrder(requiredText);
        missing.Should().NotContain(optionalText);
        missing.Should().NotContain(hiddenRequired);
        missing.Should().NotContain(gate);
    }

    [HumansFact]
    public void RequiredUnanswered_AnyAnswerKind_CountsAsAnswered()
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

    [HumansFact]
    public void RequiredUnanswered_InformationItem_IsNeverRequired()
    {
        var information = Q(
            Guid.NewGuid(), 1, 1, SurveyQuestionType.Information, required: true);

        SurveyWizardFlow.RequiredUnanswered(
                [information],
                new Dictionary<Guid, AnswerState>())
            .Should().BeEmpty();
    }

    [HumansFact]
    public void RequiredUnanswered_Grid_NeedsEveryRow()
    {
        var id = Guid.NewGuid();
        var visible = new[] { Grid(id, GridSelectionMode.Multiple) };
        var incomplete = new Dictionary<Guid, AnswerState>
        {
            [id] = new AnswerState(
                [], null, null,
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["monday"] = ["morning"],
                }),
        };

        SurveyWizardFlow.RequiredUnanswered(visible, incomplete).Should().ContainSingle().Which.Should().Be(id);

        incomplete[id] = incomplete[id] with
        {
            Grid = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["monday"] = ["morning"],
                ["tuesday"] = ["afternoon"],
            },
        };

        SurveyWizardFlow.RequiredUnanswered(visible, incomplete).Should().BeEmpty();
    }

    [HumansFact]
    public void RequiredUnanswered_SingleModeGrid_NeedsOnePerRow()
    {
        var id = Guid.NewGuid();
        var visible = new[] { Grid(id, GridSelectionMode.Single) };
        var answers = new Dictionary<Guid, AnswerState>
        {
            [id] = new AnswerState(
                [], null, null,
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["monday"] = ["morning", "afternoon"],
                    ["tuesday"] = ["afternoon"],
                }),
        };

        SurveyWizardFlow.RequiredUnanswered(visible, answers).Should().ContainSingle().Which.Should().Be(id);
    }

    [HumansFact]
    public void ToAnswerInputs_NonGridAnswer_KeepsGridNull()
    {
        var id = Guid.NewGuid();
        var answers = new Dictionary<string, SurveyWizardAnswer>(StringComparer.Ordinal)
        {
            [id.ToString()] = new() { TextValue = "hello" },
        };

        var input = SurveyWizardFlow.ToAnswerInputs(answers).Should().ContainSingle().Subject;

        input.TextValue.Should().Be("hello");
        input.GridSelections.Should().BeNull();
    }
}
