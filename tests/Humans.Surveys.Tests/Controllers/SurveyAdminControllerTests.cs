using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Surveys.Authorization;
using Humans.Surveys.Controllers;
using Humans.Surveys.Domain;
using Humans.Surveys.Models;
using Humans.Surveys.Services;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Humans.Surveys.Contracts;
using Humans.Testing;
using System.Security.Claims;

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
            Substitute.For<IAuthorizationService>(),
            Substitute.For<IStringLocalizer<SurveysResource>>(),
            NullLogger<SurveyAdminController>.Instance);

        var result = await sut.Send(surveyId, Xunit.TestContext.Current.CancellationToken);

        var model = result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<SurveySendViewModel>().Subject;
        model.AudienceTeamName.Should().Be("Archived Team");
        await teams.Received(1).GetTeamAsync(teamId, Arg.Any<CancellationToken>());
        await teams.DidNotReceive().GetTeamsAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RankedAvailability_logs_a_rejected_update()
    {
        var surveyId = Guid.NewGuid();
        var questionId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.SetRankedAvailabilityAsync(
                surveyId,
                questionId,
                Arg.Any<IReadOnlyList<string>>(),
                actorId,
                Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("The vote is still open."));
        var logger = new CapturingLogger<SurveyAdminController>();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, actorId.ToString())],
                "test")),
        };
        var sut = new SurveyAdminController(
            surveys,
            Substitute.For<ITeamServiceRead>(),
            Substitute.For<IUserServiceRead>(),
            Substitute.For<IAuthorizationService>(),
            Substitute.For<IStringLocalizer<SurveysResource>>(),
            logger)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
        };

        var result = await sut.RankedAvailability(
            surveyId,
            questionId,
            [],
            Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectToActionResult>();
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain(surveyId.ToString())
            .And.Contain(questionId.ToString())
            .And.Contain("The vote is still open.");
    }

    private static SurveyAdminController CreateController(
        ISurveyService surveys,
        ITeamServiceRead? teams = null,
        IAuthorizationService? authorizationService = null,
        Guid? userId = null,
        bool isBoardOrAdmin = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString()) };
        if (isBoardOrAdmin) claims.Add(new Claim(ClaimTypes.Role, RoleNames.Board));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return new(
            surveys,
            teams ?? Substitute.For<ITeamServiceRead>(),
            Substitute.For<IUserServiceRead>(),
            authorizationService ?? Substitute.For<IAuthorizationService>(),
            Substitute.For<IStringLocalizer<SurveysResource>>(),
            NullLogger<SurveyAdminController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
        };
    }

    private static SurveyEditInput Editable(
        string title,
        string intro = "",
        bool allowAnonymous = false,
        IReadOnlyList<QuestionInput>? questions = null) =>
        new(
            Text(title),
            string.IsNullOrEmpty(intro) ? LocalizedText.Empty : Text(intro),
            LocalizedText.Empty,
            LocalizedText.Empty,
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

    // ── Self-service approval gate (Workgroups §11): author-scoped visibility, per-id denial ──

    [HumansFact]
    public async Task Index_scopes_to_the_callers_own_surveys_for_a_plain_author()
    {
        var authorId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.GetAdminSummariesAsync(Arg.Any<SurveyViewer>(), Arg.Any<CancellationToken>())
            .Returns(new List<SurveyAdminSummary>
            {
                new(Guid.NewGuid(), "Mine", SurveyStatus.Draft, 0, 0, authorId, null, null),
            });
        var sut = CreateController(surveys, userId: authorId, isBoardOrAdmin: false);

        var result = await sut.Index(Xunit.TestContext.Current.CancellationToken);

        var model = result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<SurveyAdminIndexViewModel>().Subject;
        model.IsBoardOrAdmin.Should().BeFalse();
        await surveys.Received(1).GetAdminSummariesAsync(
            Arg.Is<SurveyViewer>(v => v.UserId == authorId && !v.IsBoardOrAdmin), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Edit_denies_a_non_owner_non_boardOrAdmin_human_by_id()
    {
        var authorId = Guid.NewGuid();
        var strangerId = Guid.NewGuid();
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Draft, Editable("Someone else's survey"), authorId));
        var sut = CreateController(surveys, authorizationService: RealAuthorizationService(), userId: strangerId, isBoardOrAdmin: false);

        var result = await sut.Edit(surveyId, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ForbidResult>();
    }

    [HumansFact]
    public async Task Edit_allows_the_owner_to_edit_their_own_draft()
    {
        var authorId = Guid.NewGuid();
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Draft, Editable("Mine"), authorId));
        var sut = CreateController(surveys, authorizationService: RealAuthorizationService(), userId: authorId, isBoardOrAdmin: false);

        var result = await sut.Edit(surveyId, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>();
    }

    [HumansFact]
    public async Task Submit_denies_a_non_owner_by_id()
    {
        var authorId = Guid.NewGuid();
        var strangerId = Guid.NewGuid();
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Draft, Editable("Someone else's survey"), authorId));
        var sut = CreateController(surveys, authorizationService: RealAuthorizationService(), userId: strangerId, isBoardOrAdmin: false);

        var result = await sut.Submit(surveyId, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ForbidResult>();
        await surveys.DidNotReceive().SubmitForApprovalAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Submit_lets_the_owner_submit_their_own_draft()
    {
        var authorId = Guid.NewGuid();
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Draft, Editable("Mine"), authorId));
        var sut = CreateController(surveys, authorizationService: RealAuthorizationService(), userId: authorId, isBoardOrAdmin: false);

        var result = await sut.Submit(surveyId, Xunit.TestContext.Current.CancellationToken);

        // Index, not Edit: the submit leaves the survey PendingApproval, which the authorization
        // handler no longer lets an ordinary author edit — an Edit redirect would land on a 403.
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(SurveyAdminController.Index));
        await surveys.Received(1).SubmitForApprovalAsync(surveyId, authorId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Results_denies_a_non_owner_non_boardOrAdmin_human_by_id()
    {
        var authorId = Guid.NewGuid();
        var strangerId = Guid.NewGuid();
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Closed, Editable("Someone else's survey"), authorId));
        var sut = CreateController(surveys, authorizationService: RealAuthorizationService(), userId: strangerId, isBoardOrAdmin: false);

        var result = await sut.Results(surveyId, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ForbidResult>();
    }

    [HumansFact]
    public async Task Results_denies_the_owner_before_their_own_survey_closes()
    {
        var authorId = Guid.NewGuid();
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Open, Editable("Mine"), authorId));
        var sut = CreateController(surveys, authorizationService: RealAuthorizationService(), userId: authorId, isBoardOrAdmin: false);

        var result = await sut.Results(surveyId, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ForbidResult>();
    }

    [HumansFact]
    public async Task Results_allows_the_owner_after_their_own_survey_closes()
    {
        var authorId = Guid.NewGuid();
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Closed, Editable("Mine"), authorId));
        surveys.GetScopedResultsAsync(surveyId, Arg.Any<SurveyResultsScope>(), Arg.Any<CancellationToken>())
            .Returns((SurveyScopedResults?)null);
        var sut = CreateController(surveys, authorizationService: RealAuthorizationService(), userId: authorId, isBoardOrAdmin: false);

        var result = await sut.Results(surveyId, scope: SurveyResultsScope.Combined, ct: Xunit.TestContext.Current.CancellationToken);

        // Not a Forbid: it proceeds to the (stubbed-null) results lookup and 404s instead.
        result.Should().BeOfType<NotFoundResult>();
    }

    /// <summary>
    /// A real, unmocked <see cref="IAuthorizationService"/> that runs the actual
    /// <see cref="SurveyAuthorizationHandler"/> — these tests exercise the production
    /// authorization decision, not a stand-in.
    /// </summary>
    private static IAuthorizationService RealAuthorizationService() => new FakeSurveyAuthorizationService();

    private sealed class FakeSurveyAuthorizationService : IAuthorizationService
    {
        private readonly SurveyAuthorizationHandler _handler = new();

        public async Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements)
        {
            var context = new AuthorizationHandlerContext(requirements, user, resource);
            await _handler.HandleAsync(context);
            return context.HasSucceeded ? AuthorizationResult.Success() : AuthorizationResult.Failed();
        }

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            throw new NotSupportedException("Policy-name authorization is not used by SurveyAdminController's resource checks.");
    }
}
