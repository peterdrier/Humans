using AwesomeAssertions;
using Humans.Surveys.Services;
using Humans.Surveys.Domain;
using Humans.Surveys.Models;
using Humans.Surveys.Contracts;

namespace Humans.Surveys.Tests.Models;

/// <summary>The builder's structured show-if rows must round-trip to/from <see cref="BranchCondition"/>.</summary>
public sealed class SurveyBuilderViewModelTests
{
    [HumansFact]
    public void Invitation_email_copy_round_trips_through_the_builder()
    {
        var detail = new SurveyDetail(
            Guid.NewGuid(),
            SurveyStatus.Draft,
            new SurveyEditInput(
                L("Survey"),
                LocalizedText.Empty,
                LocalizedText.Empty,
                L("Choose a date"),
                L("Tell us what works."),
                "en",
                false,
                null,
                null,
                null,
                null,
                null,
                null,
                []));

        var vm = SurveyBuilderViewModel.FromDetail(
            detail, [], NodaTime.DateTimeZone.Utc);
        var roundTripped = vm.ToEditInput(NodaTime.DateTimeZone.Utc);

        roundTripped.InvitationEmailSubject.Resolve("en", "en")
            .Should().Be("Choose a date");
        roundTripped.InvitationEmailMessage.Resolve("en", "en")
            .Should().Be("Tell us what works.");
    }

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

    [HumansFact]
    public void Grid_configuration_round_trips_through_the_builder()
    {
        var input = new QuestionInput(
            Guid.NewGuid(), 1, 0, SurveyQuestionType.Grid,
            L("Which dates work?"), LocalizedText.Empty, true, null, null,
            LocalizedText.Empty, LocalizedText.Empty, null,
            [
                new OptionInput(Guid.NewGuid(), 0, "morning", L("Morning")),
                new OptionInput(Guid.NewGuid(), 1, "afternoon", L("Afternoon")),
            ],
            GridSelectionMode.Multiple,
            [
                new GridRowInput("monday", L("Monday")),
                new GridRowInput("tuesday", L("Tuesday")),
            ]);

        var vm = SurveyQuestionBuilderViewModel.FromInput(input);
        var roundTripped = vm.ToInput(0);

        vm.GridSelectionMode.Should().Be(GridSelectionMode.Multiple);
        vm.GridRows.Select(row => row.Value).Should().ContainInOrder("monday", "tuesday");
        roundTripped.GridSelectionMode.Should().Be(GridSelectionMode.Multiple);
        roundTripped.GridRows!.Select(row => row.Value).Should().ContainInOrder("monday", "tuesday");
        roundTripped.Options.Select(option => option.Value).Should().ContainInOrder("morning", "afternoon");
    }

    [HumansFact]
    public void Grid_configuration_does_not_invent_an_omitted_selection_mode()
    {
        var vm = new SurveyQuestionBuilderViewModel
        {
            Type = SurveyQuestionType.Grid,
        };

        vm.ToInput(0).GridSelectionMode.Should().BeNull();
    }

    [HumansFact]
    public void Information_images_round_trip_through_the_builder()
    {
        var imageId = Guid.NewGuid();
        var input = new QuestionInput(
            Guid.NewGuid(), 1, 0, SurveyQuestionType.Information,
            L("Weather context"), L("**Forecast details**"), false, null, null,
            LocalizedText.Empty, LocalizedText.Empty, null, [],
            InformationImages:
            [
                new InformationImageInput(
                    imageId,
                    L("Temperature"),
                    L("Temperature forecast table"),
                    "uploads/surveys/survey/question/image.png",
                    "image/png",
                    "temperature.png"),
            ]);

        var vm = SurveyQuestionBuilderViewModel.FromInput(input);
        var roundTripped = vm.ToInput(0);

        var image = vm.InformationImages.Should().ContainSingle().Subject;
        image.ExistingStoragePath.Should().Be("uploads/surveys/survey/question/image.png");
        var mapped = roundTripped.InformationImages.Should().ContainSingle().Subject;
        mapped.Id.Should().Be(imageId);
        mapped.Label.Resolve("en", "en").Should().Be("Temperature");
        mapped.AltText.Resolve("en", "en").Should().Be("Temperature forecast table");
    }

    [HumansFact]
    public void ToEditInput_uses_the_posted_question_list_order_within_each_page()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var thirdId = Guid.NewGuid();
        var vm = new SurveyBuilderViewModel
        {
            Title = new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "Survey" },
            Questions =
            [
                new SurveyQuestionBuilderViewModel { Id = secondId, PageNumber = 2, Prompt = Dict("Second") },
                new SurveyQuestionBuilderViewModel { Id = firstId, PageNumber = 1, Prompt = Dict("First") },
                new SurveyQuestionBuilderViewModel { Id = thirdId, PageNumber = 2, Prompt = Dict("Third") },
            ],
        };

        var questions = vm.ToEditInput(NodaTime.DateTimeZone.Utc).Questions;

        questions.Select(question => question.Id).Should().ContainInOrder(secondId, firstId, thirdId);
        questions.Select(question => question.Order).Should().ContainInOrder(0, 0, 1);
    }

    private static LocalizedText L(string value)
        => new(Dict(value));

    private static Dictionary<string, string> Dict(string value)
        => new(StringComparer.Ordinal) { ["en"] = value };
}
