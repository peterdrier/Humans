using System.Globalization;
using System.Resources;
using AwesomeAssertions;
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
using System.Reflection;
using System.Security.Claims;

namespace Humans.Surveys.Tests.Controllers;

public sealed class SurveyAdminControllerTests
{
    [HumansTheory]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, true)]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(true, false)]
    public async Task Export_PassesActorOnlyAfterResultsAuthorization(bool json, bool authorized)
    {
        var actor = Guid.NewGuid();
        var surveyId = Guid.NewGuid();
        var ct = Xunit.TestContext.Current.CancellationToken;
        var surveys = Substitute.For<ISurveyService>();
        surveys.GetForEditAsync(surveyId, ct).Returns(new SurveyDetail(
            surveyId, SurveyStatus.Closed, Editable("Mine"), authorized ? actor : Guid.NewGuid()));
        surveys.GetResponseExportAsync(surveyId, actor, ct)
            .Returns(new SurveyResponseExport(surveyId, "Mine", "en", [], []));
        var sut = CreateController(surveys, authorizationService: RealAuthorizationService(), userId: actor);

        var result = json ? await sut.ExportJson(surveyId, ct) : await sut.ExportCsv(surveyId, ct);

        if (authorized)
        {
            result.Should().BeOfType<FileContentResult>();
            await surveys.Received(1).GetResponseExportAsync(surveyId, actor, ct);
        }
        else
        {
            result.Should().BeOfType<ForbidResult>();
            await surveys.DidNotReceiveWithAnyArgs().GetResponseExportAsync(default, default);
        }
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Save_reports_localized_success_to_an_ordinary_author(bool existing)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var authorId = Guid.NewGuid();
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.CreateAsync(Arg.Any<SurveyEditInput>(), authorId, Arg.Any<CancellationToken>()).Returns(new SurveySaveResult(surveyId));
        surveys.UpdateAsync(surveyId, Arg.Any<SurveyEditInput>(), Arg.Any<SurveyViewer>(), Arg.Any<CancellationToken>())
            .Returns(new SurveySaveResult(surveyId));
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Draft, Editable("Mine"), authorId));
        var localizer = SpanishLocalizer();
        var sut = CreateController(surveys, authorizationService: RealAuthorizationService(), userId: authorId, localizer: localizer);

        var result = await sut.Save(new SurveyBuilderViewModel
        {
            Id = existing ? surveyId : null,
            Title = new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "Mine" },
        }, null, ct);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be(nameof(SurveyAdminController.Edit));
        sut.TempData[TempDataKeys.SuccessMessage].Should().Be(existing ? "Encuesta guardada." : "Encuesta creada.");
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Save_localizes_refusals_and_propagates_dependency_failures(bool dependencyFailure)
    {
        var actor = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        if (dependencyFailure)
        {
            surveys.CreateAsync(Arg.Any<SurveyEditInput>(), actor, Arg.Any<CancellationToken>())
                .Returns<SurveySaveResult>(_ => throw new InvalidOperationException("Private storage diagnostic"));
        }
        else
        {
            surveys.CreateAsync(Arg.Any<SurveyEditInput>(), actor, Arg.Any<CancellationToken>())
                .Returns(new SurveySaveResult(Error: new("Surveys_Validation_SubjectLength", "English diagnostic", 200, "ca")));
        }
        var localizer = SpanishLocalizer();
        var sut = CreateController(surveys, userId: actor, localizer: localizer);
        var model = new SurveyBuilderViewModel
        {
            Title = new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "Entered title" },
        };

        if (dependencyFailure)
        {
            Func<Task> act = async () => await sut.Save(model, "save-translate", Xunit.TestContext.Current.CancellationToken);
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Private storage diagnostic");
            sut.ModelState.Should().BeEmpty();
            sut.TempData.Should().BeEmpty();
            await surveys.DidNotReceiveWithAnyArgs().PreFillTranslationsAsync(default, default!, default);
            return;
        }

        var result = await sut.Save(model, "save-translate", Xunit.TestContext.Current.CancellationToken);

        var view = result.Should().BeOfType<ViewResult>().Which;
        view.ViewName.Should().Be("Builder");
        view.Model.Should().BeSameAs(model);
        var error = sut.ModelState[string.Empty]!.Errors.Should().ContainSingle().Subject.ErrorMessage;
        error.Should().Be(localizer["Surveys_Validation_SubjectLength", 200, "ca"].Value);
        sut.TempData.Should().NotContainKey(TempDataKeys.SuccessMessage);
        await surveys.DidNotReceiveWithAnyArgs().PreFillTranslationsAsync(default, default!, default);
    }

    [HumansTheory]
    [Xunit.InlineData("validation")]
    [Xunit.InlineData("io")]
    [Xunit.InlineData("http")]
    [Xunit.InlineData("provider-cancellation")]
    [Xunit.InlineData("request-cancellation")]
    public async Task Save_translation_failure_reports_the_committed_save_without_exposing_the_exception(string failureKind)
    {
        var actor = Guid.NewGuid();
        var id = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.CreateAsync(Arg.Any<SurveyEditInput>(), actor, Arg.Any<CancellationToken>())
            .Returns(new SurveySaveResult(id));
        using var request = CancellationTokenSource.CreateLinkedTokenSource(Xunit.TestContext.Current.CancellationToken);
        Exception failure = failureKind switch
        {
            "io" => new IOException("Private translation diagnostic"),
            "http" => new HttpRequestException("Private translation diagnostic"),
            "provider-cancellation" or "request-cancellation" => new OperationCanceledException("Private translation diagnostic"),
            _ => new InvalidOperationException("Private translation diagnostic")
        };
        surveys.PreFillTranslationsAsync(id, Arg.Any<IReadOnlyList<string>>(), Arg.Any<SurveyViewer>(), request.Token)
            .Returns<int>(_ =>
            {
                if (string.Equals(failureKind, "request-cancellation", StringComparison.Ordinal)) request.Cancel();
                throw failure;
            });
        var localizer = SpanishLocalizer();
        var sut = CreateController(surveys, userId: actor, localizer: localizer);

        var model = new SurveyBuilderViewModel
        {
            Title = new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "Saved" },
        };
        if (string.Equals(failureKind, "request-cancellation", StringComparison.Ordinal))
        {
            Func<Task> act = async () => await sut.Save(model, "save-translate", request.Token);
            (await act.Should().ThrowAsync<OperationCanceledException>()).Which.Should().BeSameAs(failure);
            sut.TempData.Should().BeEmpty();
            await surveys.Received(1).CreateAsync(Arg.Any<SurveyEditInput>(), actor, request.Token);
            return;
        }

        var result = await sut.Save(model, "save-translate", request.Token);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Which;
        redirect.ActionName.Should().Be(nameof(SurveyAdminController.Edit));
        redirect.RouteValues!["id"].Should().Be(id);
        sut.TempData[TempDataKeys.ErrorMessage].Should().Be(localizer["Surveys_Admin_Builder_Saved_TranslationFailed"].Value);
        sut.ModelState.IsValid.Should().BeTrue();
        await surveys.Received(1).CreateAsync(Arg.Any<SurveyEditInput>(), actor, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Preview_email_reports_localized_success_to_the_ordinary_author()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var authorId = Guid.NewGuid();
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Draft, Editable("Mine"), authorId));
        var preview = Substitute.For<ISurveyPreviewEmailService>();
        preview.SendToUserAsync(surveyId, authorId, Arg.Any<CancellationToken>())
            .Returns(new SurveyPreviewEmailSendResult("author@example.org"));
        var localizer = SpanishLocalizer();
        var sut = CreateController(surveys, authorizationService: RealAuthorizationService(), userId: authorId, localizer: localizer);

        var result = await sut.SendPreviewEmail(surveyId, preview, ct);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be(nameof(SurveyAdminController.Edit));
        sut.TempData[TempDataKeys.SuccessMessage].Should().Be("Correo de vista previa de la encuesta en cola para author@example.org.");
        await preview.Received(1).SendToUserAsync(surveyId, authorId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public void Save_allows_five_valid_information_images_without_accepting_a_larger_request()
    {
        var limit = typeof(SurveyAdminController).GetMethod(nameof(SurveyAdminController.Save))!
            .GetCustomAttribute<RequestSizeLimitAttribute>();

        limit.Should().NotBeNull();
        typeof(RequestSizeLimitAttribute)
            .GetField("_bytes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(limit)
            .Should().Be(55L * 1024 * 1024);
    }

    [HumansTheory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task Review_does_not_disguise_unexpected_dependency_failures_as_refusals(bool approve)
    {
        var id = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        var failure = new InvalidOperationException("Unexpected persistence failure.");
        if (approve)
            surveys.ApproveAndSendAsync(id, Arg.Any<SurveyViewer>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<SurveyReviewResult>(failure));
        else
            surveys.RejectAsync(id, Arg.Any<SurveyViewer>(), "note", Arg.Any<CancellationToken>())
                .Returns(Task.FromException<SurveyReviewResult>(failure));
        var sut = CreateController(surveys, isBoardOrAdmin: true);

        var act = async () => approve
            ? await sut.Approve(id, Xunit.TestContext.Current.CancellationToken)
            : await sut.Reject(id, "note", Xunit.TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        sut.TempData.Should().BeEmpty();
        sut.ModelState.IsValid.Should().BeTrue();
    }

    [HumansTheory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task Open_continues_to_send_only_after_a_successful_transition(bool succeeded)
    {
        var id = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        const string reason = "A closed Asociado vote cannot be reopened.";
        surveys.OpenAsync(id, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(succeeded ? new SurveyReviewResult() : new SurveyReviewResult(Refusal: reason));
        var sut = CreateController(surveys, isBoardOrAdmin: true);

        var result = await sut.Open(id, continueToSend: true, Xunit.TestContext.Current.CancellationToken);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Which;
        redirect.ActionName.Should().Be(succeeded ? nameof(SurveyAdminController.Send) : nameof(SurveyAdminController.Edit));
        redirect.RouteValues!["id"].Should().Be(id);
        if (succeeded)
            sut.TempData[TempDataKeys.SuccessMessage].Should().Be("Survey opened.");
        else
        {
            sut.TempData[TempDataKeys.ErrorMessage].Should().Be(reason);
            sut.TempData.Should().NotContainKey(TempDataKeys.SuccessMessage);
        }
    }

    [HumansTheory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task StatusTransition_does_not_disguise_unexpected_failures_as_refusals(bool open)
    {
        var id = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        var failure = new InvalidOperationException("Unexpected status persistence failure.");
        if (open)
            surveys.OpenAsync(id, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<SurveyReviewResult>(failure));
        else
            surveys.CloseAsync(id, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<SurveyReviewResult>(failure));
        var sut = CreateController(surveys, isBoardOrAdmin: true);

        var act = () => open
            ? sut.Open(id, continueToSend: true, Xunit.TestContext.Current.CancellationToken)
            : sut.Close(id, Xunit.TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        sut.TempData.Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task SendInvites_displays_expected_refusals_or_invitation_totals(bool refused)
    {
        var id = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        const string reason = "Invitations can only be sent for an Open survey.";
        surveys.SendInvitesAsync(id, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(refused ? new SendResult(0, 0, 0, reason) : new SendResult(3, 2, 1));
        var sut = CreateController(surveys, isBoardOrAdmin: true);

        var result = await sut.SendInvites(id, Xunit.TestContext.Current.CancellationToken);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Which;
        redirect.ActionName.Should().Be(nameof(SurveyAdminController.Send));
        redirect.RouteValues!["id"].Should().Be(id);
        if (refused)
        {
            sut.TempData[TempDataKeys.ErrorMessage].Should().Be(reason);
            sut.TempData.Should().NotContainKey(TempDataKeys.SuccessMessage);
        }
        else
        {
            sut.TempData[TempDataKeys.SuccessMessage].Should().Be("Sent 3 new invitation(s); 2 email(s) queued, 1 failed.");
            sut.TempData.Should().NotContainKey(TempDataKeys.ErrorMessage);
        }
    }

    [HumansFact]
    public async Task SendInvites_does_not_disguise_unexpected_failures_as_refusals()
    {
        var id = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        var failure = new InvalidOperationException("Unexpected invitation persistence failure.");
        surveys.SendInvitesAsync(id, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<SendResult>(failure));
        var sut = CreateController(surveys, isBoardOrAdmin: true);

        var act = () => sut.SendInvites(id, Xunit.TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        sut.TempData.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Approve_refusal_returns_to_queue_with_the_reason()
    {
        var id = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        const string reason = "This survey's audience resolves to nobody to invite.";
        surveys.ApproveAndSendAsync(id, Arg.Any<SurveyViewer>(), Arg.Any<CancellationToken>())
            .Returns(new SurveyReviewResult(Refusal: reason));
        var sut = CreateController(surveys, isBoardOrAdmin: true);

        var result = await sut.Approve(id, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be(nameof(SurveyAdminController.Queue));
        sut.TempData[TempDataKeys.ErrorMessage].Should().Be(reason);
        sut.TempData.Should().NotContainKey(TempDataKeys.SuccessMessage);
    }

    [HumansFact]
    public async Task Reject_invalid_note_renders_queue_with_original_text_and_error()
    {
        var surveyId = Guid.NewGuid();
        var note = new string('x', 4001);
        var surveys = Substitute.For<ISurveyService>();
        surveys.GetPendingApprovalQueueAsync(Arg.Any<CancellationToken>())
            .Returns([new SurveyPendingApprovalItem(surveyId, "Survey", Guid.NewGuid(), "Author", null)]);
        surveys.RejectAsync(surveyId, Arg.Any<SurveyViewer>(), note, Arg.Any<CancellationToken>())
            .Returns(new SurveyReviewResult(Refusal: "A rejection note must be 4000 characters or fewer."));
        var sut = CreateController(surveys, isBoardOrAdmin: true);

        var result = await sut.Reject(surveyId, note, Xunit.TestContext.Current.CancellationToken);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        view.ViewName.Should().Be(nameof(SurveyAdminController.Queue));
        view.Model.Should().BeOfType<SurveyPendingApprovalViewModel>().Subject.Items
            .Should().ContainSingle().Which.Id.Should().Be(surveyId);
        view.ViewData[$"RejectionNote:{surveyId}"].Should().Be(note);
        sut.ModelState.IsValid.Should().BeFalse();
        sut.ModelState[string.Empty]!.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Contain("4000");
        sut.TempData.Should().BeEmpty("oversized text must not enter the TempData cookie");
    }

    [HumansFact]
    public async Task Preview_renders_a_draft_survey_through_the_respondent_intro()
    {
        var surveyId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(
                surveyId,
                SurveyStatus.Draft,
                Editable(title: "Preview me", intro: "Welcome", allowAnonymous: true),
                authorId));
        // Rehearsal is the author's own, so the real handler decides it here, not a stand-in.
        var sut = CreateController(surveys, authorizationService: RealAuthorizationService(), userId: authorId);

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
        var authorId = Guid.NewGuid();
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
                Editable(title: "Pages", questions: questions),
                authorId));
        var sut = CreateController(surveys, authorizationService: RealAuthorizationService(), userId: authorId);

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
        var teams = Substitute.For<ITeamService>();
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
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Open, editable, Guid.NewGuid()));
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
            Substitute.For<IUserService>(),
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

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task RankedAvailability_logs_refusals_and_propagates_dependency_failures(bool dependencyFailure)
    {
        var surveyId = Guid.NewGuid();
        var questionId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.SetRankedAvailabilityAsync(
                surveyId,
                questionId,
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Is<SurveyViewer>(v => v.UserId == actorId),
                Arg.Any<CancellationToken>())
            .Returns(_ => dependencyFailure
                ? throw new InvalidOperationException("The vote is still open.")
                : new SurveySaveResult(Error: new(null, "The vote is still open.")));
        var logger = new CapturingLogger<SurveyAdminController>();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, actorId.ToString())],
                "test")),
        };
        var sut = new SurveyAdminController(
            surveys,
            Substitute.For<ITeamService>(),
            Substitute.For<IUserService>(),
            Substitute.For<IAuthorizationService>(),
            Substitute.For<IStringLocalizer<SurveysResource>>(),
            logger)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
        };

        if (dependencyFailure)
        {
            Func<Task> act = async () => await sut.RankedAvailability(
                surveyId, questionId, [], Xunit.TestContext.Current.CancellationToken);
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("The vote is still open.");
            sut.TempData.Should().BeEmpty();
            logger.Entries.Should().BeEmpty();
            return;
        }

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

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Preview_dependency_failure_propagates_without_refusal_feedback(bool send)
    {
        var authorId = Guid.NewGuid();
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Draft, Editable("Mine"), authorId));
        var preview = Substitute.For<ISurveyPreviewEmailService>();
        preview.PreviewForUserAsync(surveyId, authorId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<SurveyEmailPreviewResult>(new InvalidOperationException("Private preview diagnostic")));
        preview.SendToUserAsync(surveyId, authorId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<SurveyPreviewEmailSendResult>(new InvalidOperationException("Private preview diagnostic")));
        var localizer = SpanishLocalizer();
        var sut = CreateController(surveys, authorizationService: RealAuthorizationService(), userId: authorId,
            localizer: localizer);

        Func<Task> act = async () =>
        {
            if (send)
                await sut.SendPreviewEmail(surveyId, preview, Xunit.TestContext.Current.CancellationToken);
            else
                await sut.PreviewEmail(surveyId, preview, Xunit.TestContext.Current.CancellationToken);
        };

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Private preview diagnostic");
        sut.TempData.Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Preview_refusal_uses_translated_feedback_without_success(bool send)
    {
        var authorId = Guid.NewGuid();
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Draft, Editable("Mine"), authorId));
        var preview = Substitute.For<ISurveyPreviewEmailService>();
        preview.PreviewForUserAsync(surveyId, authorId, Arg.Any<CancellationToken>())
            .Returns(new SurveyEmailPreviewResult(ErrorKey: "Surveys_PreviewNoEmail"));
        preview.SendToUserAsync(surveyId, authorId, Arg.Any<CancellationToken>())
            .Returns(new SurveyPreviewEmailSendResult(ErrorKey: "Surveys_PreviewNoEmail"));
        var localizer = SpanishLocalizer();
        var sut = CreateController(surveys, authorizationService: RealAuthorizationService(), userId: authorId,
            localizer: localizer);

        var result = send
            ? await sut.SendPreviewEmail(surveyId, preview, Xunit.TestContext.Current.CancellationToken)
            : await sut.PreviewEmail(surveyId, preview, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectToActionResult>();
        sut.TempData[TempDataKeys.ErrorMessage].Should().Be(localizer["Surveys_PreviewNoEmail"].Value);
        sut.TempData.Should().NotContainKey(TempDataKeys.SuccessMessage);
    }

    private static IStringLocalizer<SurveysResource> SpanishLocalizer()
    {
        var localizer = Substitute.For<IStringLocalizer<SurveysResource>>();
        var resources = new ResourceManager(typeof(SurveysResource));
        var culture = CultureInfo.GetCultureInfo("es");
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), resources.GetString(ci.Arg<string>(), culture)!));
        localizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), string.Format(culture, resources.GetString(ci.Arg<string>(), culture)!, ci.Arg<object[]>())));
        return localizer;
    }

    private static SurveyAdminController CreateController(
        ISurveyService surveys,
        ITeamService? teams = null,
        IAuthorizationService? authorizationService = null,
        Guid? userId = null,
        bool isBoardOrAdmin = false,
        IStringLocalizer<SurveysResource>? localizer = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString()) };
        if (isBoardOrAdmin) claims.Add(new Claim(ClaimTypes.Role, RoleNames.Board));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return new(
            surveys,
            teams ?? Substitute.For<ITeamService>(),
            Substitute.For<IUserService>(),
            authorizationService ?? Substitute.For<IAuthorizationService>(),
            localizer ?? Substitute.For<IStringLocalizer<SurveysResource>>(),
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
    public async Task Edit_hides_the_run_controls_from_an_author_who_is_not_on_the_Board()
    {
        var authorId = Guid.NewGuid();
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Draft, Editable("Mine"), authorId));
        var sut = CreateController(surveys, authorizationService: RealAuthorizationService(), userId: authorId, isBoardOrAdmin: false);

        var result = await sut.Edit(surveyId, Xunit.TestContext.Current.CancellationToken);

        var model = result.Should().BeOfType<ViewResult>().Which.Model
            .Should().BeOfType<SurveyBuilderViewModel>().Which;
        model.IsBoardOrAdmin.Should().BeFalse("Open, Close, preview and recipient review are BoardOrAdmin");
    }

    [HumansTheory]
    [Xunit.InlineData(true, false, true)]
    [Xunit.InlineData(true, true, true)]
    [Xunit.InlineData(false, true, false)]
    public async Task Builder_submit_control_matches_author_ownership(bool ownsSurvey, bool board, bool canSubmit)
    {
        var viewerId = Guid.NewGuid();
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Draft, Editable("Survey"),
                ownsSurvey ? viewerId : Guid.NewGuid()));
        var sut = CreateController(surveys, authorizationService: RealAuthorizationService(), userId: viewerId, isBoardOrAdmin: board);

        var result = await sut.Edit(surveyId, Xunit.TestContext.Current.CancellationToken);

        var model = result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<SurveyBuilderViewModel>().Which;
        model.CanSubmit.Should().Be(canSubmit);
    }

    [HumansFact]
    public async Task Invalid_save_rebuilds_submit_control_from_the_stored_owner()
    {
        var viewerId = Guid.NewGuid();
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Draft, Editable("Survey"), Guid.NewGuid()));
        var sut = CreateController(surveys, authorizationService: RealAuthorizationService(), userId: viewerId, isBoardOrAdmin: true);
        sut.ModelState.AddModelError("Title", "Required");

        var result = await sut.Save(new SurveyBuilderViewModel { Id = surveyId, CanSubmit = true }, null,
            Xunit.TestContext.Current.CancellationToken);

        var model = result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<SurveyBuilderViewModel>().Which;
        model.CanSubmit.Should().BeFalse();
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
        surveys.SubmitForApprovalAsync(surveyId, authorId, Arg.Any<CancellationToken>())
            .Returns(new SurveySubmitResult(true));
        var sut = CreateController(surveys, authorizationService: RealAuthorizationService(), userId: authorId, isBoardOrAdmin: false);

        var result = await sut.Submit(surveyId, Xunit.TestContext.Current.CancellationToken);

        // Index, not Edit: the submit leaves the survey PendingApproval, which the authorization
        // handler no longer lets an ordinary author edit — an Edit redirect would land on a 403.
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(SurveyAdminController.Index));
        await surveys.Received(1).SubmitForApprovalAsync(surveyId, authorId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Submit_dependency_failure_propagates_without_refusal_feedback()
    {
        var authorId = Guid.NewGuid();
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Draft, Editable("Mine"), authorId));
        surveys.SubmitForApprovalAsync(surveyId, authorId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<SurveySubmitResult>(new InvalidOperationException("Private provider diagnostic")));
        var localizer = SpanishLocalizer();
        var sut = CreateController(surveys, authorizationService: RealAuthorizationService(), userId: authorId,
            localizer: localizer);

        Func<Task> act = async () => await sut.Submit(surveyId, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Private provider diagnostic");
        sut.TempData.Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData("Surveys_SubmitNotAuthor")]
    [Xunit.InlineData("Surveys_SubmitNotDraft")]
    public async Task Submit_refusal_uses_translated_feedback(string errorKey)
    {
        var authorId = Guid.NewGuid();
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Draft, Editable("Mine"), authorId));
        surveys.SubmitForApprovalAsync(surveyId, authorId, Arg.Any<CancellationToken>())
            .Returns(new SurveySubmitResult(false, errorKey));
        var localizer = SpanishLocalizer();
        var sut = CreateController(surveys, authorizationService: RealAuthorizationService(), userId: authorId,
            localizer: localizer);

        await sut.Submit(surveyId, Xunit.TestContext.Current.CancellationToken);

        sut.TempData[TempDataKeys.ErrorMessage].Should().Be(localizer[errorKey].Value);
        sut.TempData.Should().NotContainKey(TempDataKeys.SuccessMessage);
    }

    [HumansTheory]
    [Xunit.InlineData("intro")]
    [Xunit.InlineData("page")]
    [Xunit.InlineData("thank-you")]
    [Xunit.InlineData("email")]
    [Xunit.InlineData("send-email")]
    public async Task Preview_denies_a_non_owner_non_boardOrAdmin_human_by_id(string route)
    {
        // Rehearsal reads the whole survey back, Drafts included — opening it to authors must not
        // open it to everyone with an account.
        var authorId = Guid.NewGuid();
        var strangerId = Guid.NewGuid();
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Draft, Editable("Someone else's survey"), authorId));
        var sut = CreateController(surveys, authorizationService: RealAuthorizationService(), userId: strangerId, isBoardOrAdmin: false);

        var previewEmails = Substitute.For<ISurveyPreviewEmailService>();
        var ct = Xunit.TestContext.Current.CancellationToken;
        var result = route switch
        {
            "page" => await sut.PreviewPage(surveyId, "en", null, ct),
            "thank-you" => await sut.PreviewThankYou(surveyId, "en", ct),
            "email" => await sut.PreviewEmail(surveyId, previewEmails, ct),
            "send-email" => await sut.SendPreviewEmail(surveyId, previewEmails, ct),
            _ => await sut.Preview(surveyId, "en", ct),
        };

        result.Should().BeOfType<ForbidResult>();
        previewEmails.ReceivedCalls().Should().BeEmpty();
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
