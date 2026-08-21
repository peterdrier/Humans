using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Surveys.Controllers;
using Humans.Surveys.Domain;
using Humans.Surveys.Models;
using Humans.Surveys.Services;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Surveys.Tests.Controllers;

public sealed class SurveyAdminControllerTests
{
    [HumansFact]
    public async Task Preview_renders_a_draft_survey_through_the_respondent_intro()
    {
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(
                surveyId,
                SurveyStatus.Draft,
                Editable(title: "Preview me", intro: "Welcome", allowAnonymous: true)));
        var sut = CreateController(surveys);

        var result = await sut.Preview(
            surveyId, "en", Xunit.TestContext.Current.CancellationToken);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        view.ViewName.Should().Be("~/Views/Survey/Intro.cshtml");
        var model = view.Model.Should().BeOfType<SurveyIntroViewModel>().Subject;
        model.IsPreview.Should().BeTrue();
        model.PreviewSurveyId.Should().Be(surveyId);
        model.Title.Should().Be("Preview me");
        model.Intro.Should().Be("Welcome");
        model.ShowAnonymitySelector.Should().BeTrue();
    }

    [HumansFact]
    public async Task PreviewPage_shows_all_authored_questions_on_the_selected_page()
    {
        var surveyId = Guid.NewGuid();
        var firstQuestionId = Guid.NewGuid();
        var conditionalQuestionId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        var questions = new[]
        {
            Question(firstQuestionId, page: 1, order: 0, prompt: "First"),
            Question(
                conditionalQuestionId,
                page: 2,
                order: 0,
                prompt: "Conditional",
                showIf: new BranchCondition
                {
                    Clauses =
                    [
                        new BranchClause
                        {
                            QuestionId = firstQuestionId,
                            Operator = BranchOperator.Answered,
                        },
                    ],
                }),
        };
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(
                surveyId,
                SurveyStatus.Closed,
                Editable(title: "Pages", questions: questions)));
        var sut = CreateController(surveys);

        var result = await sut.PreviewPage(
            surveyId, "en", page: 2, ct: Xunit.TestContext.Current.CancellationToken);

        var model = result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<SurveyPageViewModel>().Subject;
        model.IsPreview.Should().BeTrue();
        model.Page.Should().Be(2);
        model.StepNumber.Should().Be(2);
        model.Questions.Should().ContainSingle()
            .Which.Prompt.Should().Be("Conditional");
    }

    [HumansFact]
    public async Task Send_preserves_the_name_of_a_deactivated_audience_team()
    {
        var surveyId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        var teams = Substitute.For<ITeamServiceRead>();
        var editable = new SurveyEditInput(
            Text("Team survey"),
            LocalizedText.Empty,
            LocalizedText.Empty,
            "en",
            false,
            null,
            null,
            SurveyAudienceType.Team,
            teamId,
            null,
            null,
            []);
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Open, editable));
        surveys.PreviewAudienceCountAsync(surveyId, Arg.Any<CancellationToken>()).Returns(3);
        surveys.GetInviteStatusesAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SurveyInviteStatus>());
        teams.GetTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(new TeamInfo(
                teamId,
                "Archived Team",
                null,
                "archived-team",
                IsActive: false,
                IsSystemTeam: false,
                SystemTeamType.None,
                RequiresApproval: false,
                IsPublicPage: false,
                IsHidden: false,
                IsPromotedToDirectory: false,
                Instant.FromUtc(2026, 1, 1, 0, 0),
                []));
        var sut = new SurveyAdminController(
            surveys,
            teams,
            Substitute.For<IUserServiceRead>(),
            NullLogger<SurveyAdminController>.Instance);

        var result = await sut.Send(surveyId, Xunit.TestContext.Current.CancellationToken);

        var model = result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<SurveySendViewModel>().Subject;
        model.AudienceTeamName.Should().Be("Archived Team");
        await teams.Received(1).GetTeamAsync(teamId, Arg.Any<CancellationToken>());
        await teams.DidNotReceive().GetTeamsAsync(Arg.Any<CancellationToken>());
    }

    private static SurveyAdminController CreateController(
        ISurveyService surveys,
        ITeamServiceRead? teams = null) =>
        new(
            surveys,
            teams ?? Substitute.For<ITeamServiceRead>(),
            Substitute.For<IUserServiceRead>(),
            NullLogger<SurveyAdminController>.Instance);

    private static SurveyEditInput Editable(
        string title,
        string intro = "",
        bool allowAnonymous = false,
        IReadOnlyList<QuestionInput>? questions = null) =>
        new(
            Text(title),
            string.IsNullOrEmpty(intro) ? LocalizedText.Empty : Text(intro),
            LocalizedText.Empty,
            "en",
            allowAnonymous,
            null,
            null,
            null,
            null,
            null,
            null,
            questions ?? []);

    private static QuestionInput Question(
        Guid id,
        int page,
        int order,
        string prompt,
        BranchCondition? showIf = null) =>
        new(
            id,
            page,
            order,
            SurveyQuestionType.ShortText,
            Text(prompt),
            LocalizedText.Empty,
            false,
            null,
            null,
            LocalizedText.Empty,
            LocalizedText.Empty,
            showIf,
            []);

    private static LocalizedText Text(string en) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = en });
}
