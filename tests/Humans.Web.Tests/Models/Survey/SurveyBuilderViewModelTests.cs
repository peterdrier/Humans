using AwesomeAssertions;
using Humans.Application.Interfaces.Surveys;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using Humans.Web.Models;
using Xunit;

namespace Humans.Web.Tests.Models.Survey;

/// <summary>The builder's structured show-if rows must round-trip to/from <see cref="BranchCondition"/>.</summary>
public sealed class SurveyBuilderViewModelTests
{
    [HumansFact]
    public void ToInput_maps_clause_rows_to_a_branch_condition()
    {
        var gate = Guid.NewGuid();
        var vm = new SurveyQuestionBuilderViewModel
        {
            ShowIfCombine = BranchCombine.Any,
            ShowIfClauses =
            [
                new SurveyBranchClauseBuilderViewModel
                {
                    QuestionId = gate,
                    Operator = BranchOperator.Is,
                    OptionValues = ["yes", "", "maybe"], // blank entries are dropped
                },
                new SurveyBranchClauseBuilderViewModel { QuestionId = null }, // never picked → dropped
            ],
        };

        var showIf = vm.ToInput(0).ShowIf;

        showIf.Should().NotBeNull();
        showIf.Combine.Should().Be(BranchCombine.Any);
        var clause = showIf.Clauses.Should().ContainSingle().Subject;
        clause.QuestionId.Should().Be(gate);
        clause.Operator.Should().Be(BranchOperator.Is);
        clause.OptionValues.Should().BeEquivalentTo("yes", "maybe");
    }

    [HumansFact]
    public void ToInput_without_clauses_means_always_visible()
        => new SurveyQuestionBuilderViewModel().ToInput(0).ShowIf.Should().BeNull();

    [HumansFact]
    public void FromInput_round_trips_an_existing_condition_into_clause_rows()
    {
        var gate = Guid.NewGuid();
        var input = new QuestionInput(
            Guid.NewGuid(), 1, 0, SurveyQuestionType.ShortText,
            LocalizedText.Empty, LocalizedText.Empty, false, null, null,
            LocalizedText.Empty, LocalizedText.Empty,
            new BranchCondition
            {
                Combine = BranchCombine.All,
                Clauses = { new BranchClause { QuestionId = gate, Operator = BranchOperator.IsNot, OptionValues = ["no"] } },
            },
            []);

        var vm = SurveyQuestionBuilderViewModel.FromInput(input);

        vm.ShowIfCombine.Should().Be(BranchCombine.All);
        var row = vm.ShowIfClauses.Should().ContainSingle().Subject;
        row.QuestionId.Should().Be(gate);
        row.Operator.Should().Be(BranchOperator.IsNot);
        row.OptionValues.Should().BeEquivalentTo("no");

        var roundTripped = vm.ToInput(0).ShowIf;
        roundTripped!.Clauses.Single().QuestionId.Should().Be(gate);
    }
}
