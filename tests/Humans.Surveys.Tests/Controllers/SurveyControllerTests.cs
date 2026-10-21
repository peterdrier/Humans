using AwesomeAssertions;
using Humans.Surveys.Contracts;
using Humans.Surveys.Controllers;
using Humans.Surveys.Domain;
using Humans.Surveys.Models;
using Humans.Surveys.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using System.Security.Claims;
using System.Globalization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Humans.Surveys.Tests.Controllers;

/// <summary>
/// The respondent-facing wizard's closing page. Submitting flips <c>Invitation.Completed</c>, which
/// makes the invite token stop resolving — so the thank-you page cannot re-derive the survey from the
/// token and reads a completion marker left in the session at submit instead.
/// </summary>
public sealed class SurveyControllerTests
{
    private const string Token = "invite-token";

    [HumansTheory]
    [Xunit.InlineData(true, true, false, null, null)]
    [Xunit.InlineData(true, true, false, "Identified", ResponseAnonymity.Identified)]
    [Xunit.InlineData(true, true, false, "CompletionTracked", ResponseAnonymity.CompletionTracked)]
    [Xunit.InlineData(true, true, false, "Anonymous", ResponseAnonymity.Anonymous)]
    [Xunit.InlineData(false, true, false, null, ResponseAnonymity.Anonymous)]
    [Xunit.InlineData(true, false, false, null, ResponseAnonymity.Identified)]
    [Xunit.InlineData(true, false, true, null, ResponseAnonymity.CompletionTracked)]
    [Xunit.InlineData(true, true, false, "invalid", null)]
    public async Task Public_start_requires_an_explicit_choice_only_when_the_respondent_chooses_privacy(
        bool loggedIn, bool allowAnonymous, bool asociadoVote, string? posted, ResponseAnonymity? expected)
    {
        var surveyId = Guid.NewGuid();
        Guid? userId = loggedIn ? Guid.NewGuid() : null;
        var participationId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.ResolvePublicContextAsync("volunteer-survey", userId, Arg.Any<CancellationToken>()).Returns(
            new SurveyPublicContext(surveyId, Detail(surveyId, "Thanks!", allowAnonymous: allowAnonymous,
                publicSlug: "volunteer-survey", isAsociadoVote: asociadoVote)));
        surveys.StartPublicTrackedResponseAsync(surveyId, Arg.Any<Guid>(), Arg.Any<ResponseAnonymity>(), "en", Arg.Any<CancellationToken>())
            .Returns(call => new SurveyPublicStart(participationId,
                call.Arg<ResponseAnonymity>() == ResponseAnonymity.Identified ? draftId : null, []));
        var sut = CreateController(surveys, out var session, userId);
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        sut.HttpContext.RequestServices = services;
        sut.Url = Substitute.For<IUrlHelper>();
        var method = typeof(SurveyController).GetMethod(nameof(SurveyController.PublicStart))!;
        var parameter = method.GetParameters().Single(p => string.Equals(p.Name, "anonymity", StringComparison.Ordinal));
        var metadata = ((ModelMetadataProvider)services.GetRequiredService<IModelMetadataProvider>()).GetMetadataForParameter(parameter);
        var descriptor = new ControllerParameterDescriptor
        {
            Name = parameter.Name!,
            ParameterType = parameter.ParameterType,
            ParameterInfo = parameter
        };
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = parameter
        });
        var fields = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
        if (posted is not null) fields["Anonymity"] = posted;
        var binding = await services.GetRequiredService<ParameterBinder>().BindModelAsync(sut.ControllerContext, binder,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
            descriptor, metadata, value: null);
        var argument = binding.IsModelSet ? binding.Model : Activator.CreateInstance(parameter.ParameterType);

        var result = await (Task<IActionResult>)method.Invoke(sut,
            ["volunteer-survey", "en", argument, Xunit.TestContext.Current.CancellationToken])!;

        if (expected is null)
        {
            result.Should().BeOfType<BadRequestResult>();
            session.Keys.Should().BeEmpty();
            surveys.ReceivedCalls().Select(call => call.GetMethodInfo().Name).Should()
                .NotContain(nameof(ISurveyService.StartPublicTrackedResponseAsync))
                .And.NotContain(nameof(ISurveyService.IncrementPublicStartedAsync));
        }
        else
        {
            result.Should().BeOfType<RedirectToActionResult>();
            var state = SurveyWizardSession.LoadBySlug(session, "volunteer-survey");
            state.Should().NotBeNull();
            state.Anonymity.Should().Be(expected.Value);
            state.UserId.Should().Be(expected == ResponseAnonymity.Anonymous ? null : userId);
            state.DraftResponseId.Should().Be(expected == ResponseAnonymity.Identified ? draftId : null);
            await surveys.Received(1).IncrementPublicStartedAsync(surveyId, Xunit.TestContext.Current.CancellationToken);
            if (expected == ResponseAnonymity.Anonymous)
                await surveys.DidNotReceive().StartPublicTrackedResponseAsync(surveyId, Arg.Any<Guid>(),
                    Arg.Any<ResponseAnonymity>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            else
                await surveys.Received(1).StartPublicTrackedResponseAsync(surveyId, userId!.Value,
                    expected.Value, "en", Xunit.TestContext.Current.CancellationToken);
        }
    }

    [HumansTheory]
    [Xunit.InlineData(null, null)]
    [Xunit.InlineData("Page", "two")]
    [Xunit.InlineData("Answers[0].QuestionId", "not-a-guid")]
    [Xunit.InlineData("Answers[0].RatingValue", "five")]
    public async Task Public_page_post_binds_the_real_form_and_rejects_only_malformed_values(
        string? field, string? value)
    {
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.ResolvePublicContextAsync("volunteer-survey", null, Arg.Any<CancellationToken>()).Returns(
            new SurveyPublicContext(surveyId, Detail(surveyId, "Thanks!", publicSlug: "volunteer-survey")));
        surveys.AdvanceWizardAsync(
                Arg.Any<SurveyWizardState>(), Arg.Any<int>(), Arg.Any<bool>(),
                Arg.Any<IReadOnlyList<SurveyAnswerInput>>(), Arg.Any<CancellationToken>())
            .Returns(new SurveyWizardAdvanceResult(SurveyWizardOutcome.Navigated, []));
        var sut = CreateController(surveys, out var session);
        SurveyWizardSession.SaveBySlug(session, "volunteer-survey",
            new SurveyWizardState { SurveyId = surveyId, Culture = "en", Anonymity = ResponseAnonymity.Anonymous });
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        sut.HttpContext.RequestServices = services;
        sut.Url = Substitute.For<IUrlHelper>();
        // What Page.cshtml posts on the public path: no Token, blank grid/ranked values are normal.
        var fields = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["Page"] = "1",
            ["Answers[0].QuestionId"] = Guid.NewGuid().ToString(),
            ["Answers[0].RatingValue"] = "",
            ["Answers[0].GridRows[0].RowValue"] = "",
            ["Answers[0].RankedOptions[0].OptionValue"] = "",
            ["Answers[0].RankedOptions[0].Selection"] = "",
        };
        if (field is not null) fields[field] = value!;
        var method = typeof(SurveyController).GetMethod(nameof(SurveyController.PublicSubmitPage))!;
        var parameter = method.GetParameters().Single(p => string.Equals(p.Name, "model", StringComparison.Ordinal));
        var metadata = ((ModelMetadataProvider)services.GetRequiredService<IModelMetadataProvider>()).GetMetadataForParameter(parameter);
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = parameter
        });
        var binding = await services.GetRequiredService<ParameterBinder>().BindModelAsync(sut.ControllerContext, binder,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
            new ControllerParameterDescriptor { Name = parameter.Name!, ParameterType = parameter.ParameterType, ParameterInfo = parameter },
            metadata, value: null);

        var result = await sut.PublicSubmitPage(
            "volunteer-survey", (SurveyPageInputModel)binding.Model!, Xunit.TestContext.Current.CancellationToken);

        if (field is null)
        {
            result.Should().BeOfType<RedirectToActionResult>();
            await surveys.Received(1).AdvanceWizardAsync(
                Arg.Any<SurveyWizardState>(), 1, false,
                Arg.Any<IReadOnlyList<SurveyAnswerInput>>(), Arg.Any<CancellationToken>());
        }
        else
        {
            result.Should().BeOfType<BadRequestObjectResult>();
            sut.ModelState.Keys.Should().Contain(field);
            await surveys.DidNotReceiveWithAnyArgs().AdvanceWizardAsync(default!, default, default, default!, default);
        }
    }

    [HumansFact]
    public async Task Submitting_leaves_the_thank_you_marker_in_session()
    {
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.AdvanceWizardAsync(
                Arg.Any<SurveyWizardState>(), Arg.Any<int>(), Arg.Any<bool>(),
                Arg.Any<IReadOnlyList<SurveyAnswerInput>>(), Arg.Any<CancellationToken>())
            .Returns(new SurveyWizardAdvanceResult(SurveyWizardOutcome.Submitted, []));
        var sut = CreateController(surveys, out var session);
        SurveyWizardSession.Save(
            session, Token, new SurveyWizardState { SurveyId = surveyId, Culture = "es" });

        var result = await sut.SubmitPage(
            new SurveyPageInputModel { Token = Token }, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectToActionResult>().Subject.ActionName.Should().Be("ThankYou");
        SurveyWizardSession.Load(session, Token).Should().BeNull("the wizard state is cleared at submit");
        SurveyWizardSession.LoadCompleted(session, Token)
            .Should().Be(new SurveyCompletion(surveyId, "es"));
    }

    [HumansFact]
    public async Task ThankYou_shows_the_authored_copy_after_the_invitation_is_completed()
    {
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        // A spent token: submit already flipped Completed, so the answer context no longer resolves.
        surveys.ResolveAnswerContextAsync(Token, Arg.Any<CancellationToken>())
            .Returns((SurveyAnswerContext?)null);
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(Detail(surveyId, thankYou: "Thanks — see you at the gate."));
        var sut = CreateController(surveys, out var session);
        SurveyWizardSession.SaveCompleted(session, Token, new SurveyCompletion(surveyId, "en"));

        var result = await sut.ThankYou(Token, Xunit.TestContext.Current.CancellationToken);

        var model = result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<SurveyThankYouViewModel>().Subject;
        model.Title.Should().Be("Volunteer survey");
        model.ThankYou.Should().Be("Thanks — see you at the gate.");
    }

    [HumansFact]
    public async Task ThankYou_uses_the_language_the_respondent_answered_in()
    {
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(Detail(surveyId, thankYou: "Thanks!", spanishThankYou: "¡Gracias!"));
        var sut = CreateController(surveys, out var session);
        SurveyWizardSession.SaveCompleted(session, Token, new SurveyCompletion(surveyId, "es"));

        var result = await sut.ThankYou(Token, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<SurveyThankYouViewModel>().Subject
            .ThankYou.Should().Be("¡Gracias!");
    }

    [HumansFact]
    public async Task ThankYou_without_a_marker_still_resolves_a_bookmarked_link_by_token()
    {
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.ResolveAnswerContextAsync(Token, Arg.Any<CancellationToken>())
            .Returns(new SurveyAnswerContext(
                surveyId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Detail(surveyId, thankYou: "Thanks — see you at the gate."),
                [],
                DraftResponseId: null));
        var sut = CreateController(surveys, out _);

        var result = await sut.ThankYou(Token, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<SurveyThankYouViewModel>().Subject
            .ThankYou.Should().Be("Thanks — see you at the gate.");
        await surveys.DidNotReceive().GetForEditAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task PublicThankYou_reads_the_marker_for_its_own_slug_only()
    {
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.ResolvePublicContextAsync("other-survey", null, Arg.Any<CancellationToken>())
            .Returns((SurveyPublicContext?)null);
        var sut = CreateController(surveys, out var session);
        SurveyWizardSession.SaveCompletedBySlug(
            session, "open-survey", new SurveyCompletion(surveyId, "en"));

        var result = await sut.PublicThankYou(
            "other-survey", Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<SurveyThankYouViewModel>().Subject
            .ThankYou.Should().Be("Thanks!", "another slug's marker must not leak its copy");
        await surveys.DidNotReceive().GetForEditAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Identified_slug_requires_sign_in()
    {
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.ResolvePublicContextAsync("board-vote", null, Arg.Any<CancellationToken>())
            .Returns(new SurveyPublicContext(
                surveyId,
                Detail(surveyId, "Thanks!", allowAnonymous: false, publicSlug: "board-vote"),
                SurveyPublicAccess.AuthenticationRequired));
        var sut = CreateController(surveys, out _);

        var result = await sut.Public("board-vote", Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ChallengeResult>();
    }

    [HumansFact]
    public async Task Eligible_identified_slug_shows_an_identified_intro_without_anonymity_choices()
    {
        var surveyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        var detail = Detail(
            surveyId, "Thanks!", allowAnonymous: false, publicSlug: "board-vote");
        surveys.ResolvePublicContextAsync("board-vote", userId, Arg.Any<CancellationToken>())
            .Returns(new SurveyPublicContext(surveyId, detail));
        var sut = CreateController(surveys, out _, userId);

        var result = await sut.Public("board-vote", Xunit.TestContext.Current.CancellationToken);

        var model = result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<SurveyIntroViewModel>().Subject;
        model.AllowAnonymous.Should().BeFalse();
        model.ShowAnonymitySelector.Should().BeFalse();
        model.Slug.Should().Be("board-vote");
    }

    [HumansFact]
    public async Task Identified_slug_forces_identified_even_if_anonymous_is_posted()
    {
        var surveyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var participationId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        var detail = Detail(
            surveyId, "Thanks!", allowAnonymous: false, publicSlug: "board-vote");
        surveys.ResolvePublicContextAsync("board-vote", userId, Arg.Any<CancellationToken>())
            .Returns(new SurveyPublicContext(surveyId, detail));
        surveys.StartPublicTrackedResponseAsync(
                surveyId,
                userId,
                ResponseAnonymity.Identified,
                "en",
                Arg.Any<CancellationToken>())
            .Returns(new SurveyPublicStart(participationId, null, []));
        var sut = CreateController(surveys, out var session, userId);

        var result = await sut.PublicStart(
            "board-vote",
            "en",
            ResponseAnonymity.Anonymous,
            Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectToActionResult>().Subject.ActionName
            .Should().Be("PublicPage");
        var state = SurveyWizardSession.LoadBySlug(session, "board-vote");
        state.Should().NotBeNull();
        state.Anonymity.Should().Be(ResponseAnonymity.Identified);
        state.UserId.Should().Be(userId);
        state.InvitationId.Should().Be(participationId);
    }

    [HumansFact]
    public async Task Asociado_slug_forces_completion_tracking_without_linking_a_draft()
    {
        var surveyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var participationId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        var detail = Detail(
            surveyId,
            "Thanks!",
            allowAnonymous: false,
            publicSlug: "asociado-vote",
            isAsociadoVote: true);
        surveys.ResolvePublicContextAsync("asociado-vote", userId, Arg.Any<CancellationToken>())
            .Returns(new SurveyPublicContext(surveyId, detail));
        surveys.StartPublicTrackedResponseAsync(
                surveyId,
                userId,
                ResponseAnonymity.CompletionTracked,
                "en",
                Arg.Any<CancellationToken>())
            .Returns(new SurveyPublicStart(participationId, null, []));
        var sut = CreateController(surveys, out var session, userId);

        var result = await sut.PublicStart(
            "asociado-vote",
            "en",
            ResponseAnonymity.Identified,
            Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectToActionResult>().Subject.ActionName
            .Should().Be("PublicPage");
        var state = SurveyWizardSession.LoadBySlug(session, "asociado-vote");
        state.Should().NotBeNull();
        state.Anonymity.Should().Be(ResponseAnonymity.CompletionTracked);
        state.UserId.Should().Be(userId);
        state.InvitationId.Should().Be(participationId);
        state.DraftResponseId.Should().BeNull();
    }

    [HumansFact]
    public async Task Asociado_invite_start_forces_completion_tracking_and_does_not_create_a_draft()
    {
        var surveyId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.ResolveAnswerContextAsync(Token, Arg.Any<CancellationToken>())
            .Returns(new SurveyAnswerContext(
                surveyId,
                invitationId,
                userId,
                Detail(surveyId, "Thanks!", allowAnonymous: false, isAsociadoVote: true),
                [],
                DraftResponseId: null));
        var sut = CreateController(surveys, out var session);

        var result = await sut.Start(
            new SurveyStartViewModel
            {
                Token = Token,
                Culture = "en",
                Anonymity = ResponseAnonymity.Identified,
            },
            Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectToActionResult>().Subject.ActionName.Should().Be("Page");
        var state = SurveyWizardSession.Load(session, Token);
        state.Should().NotBeNull();
        state.Anonymity.Should().Be(ResponseAnonymity.CompletionTracked);
        state.UserId.Should().Be(userId);
        state.InvitationId.Should().Be(invitationId);
        state.DraftResponseId.Should().BeNull();
        await surveys.DidNotReceive().StartIdentifiedDraftAsync(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<SurveyInputMethod>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Asociado_invite_preserves_a_legacy_identified_draft_as_unlinked_wizard_state()
    {
        var surveyId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var questionId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.ResolveAnswerContextAsync(Token, Arg.Any<CancellationToken>())
            .Returns(new SurveyAnswerContext(
                surveyId,
                invitationId,
                userId,
                Detail(surveyId, "Thanks!", allowAnonymous: false, isAsociadoVote: true),
                [new SurveyDraftAnswer(questionId, ["yes"], null, null)],
                DraftResponseId: Guid.NewGuid()));
        var sut = CreateController(surveys, out var session);

        var result = await sut.Answer(Token, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectToActionResult>().Subject.ActionName.Should().Be("Page");
        var state = SurveyWizardSession.Load(session, Token);
        state.Should().NotBeNull();
        state.Anonymity.Should().Be(ResponseAnonymity.CompletionTracked);
        state.DraftResponseId.Should().BeNull();
        state.Answers[questionId.ToString()].SelectedOptionValues.Should().ContainSingle("yes");
        await surveys.DidNotReceive().StartIdentifiedDraftAsync(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<SurveyInputMethod>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Identified_invite_resume_reuses_the_existing_draft_without_a_write()
    {
        var surveyId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        surveys.ResolveAnswerContextAsync(Token, Arg.Any<CancellationToken>())
            .Returns(new SurveyAnswerContext(
                surveyId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Detail(surveyId, "Thanks!", allowAnonymous: true),
                [],
                DraftResponseId: draftId));
        var sut = CreateController(surveys, out var session);

        var result = await sut.Answer(Token, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectToActionResult>().Subject.ActionName.Should().Be("Page");
        var state = SurveyWizardSession.Load(session, Token);
        state!.Anonymity.Should().Be(ResponseAnonymity.Identified);
        state.DraftResponseId.Should().Be(draftId);
        await surveys.DidNotReceive().StartIdentifiedDraftAsync(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<SurveyInputMethod>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Public_page_rechecks_audience_and_clears_the_wizard_when_access_is_lost()
    {
        var surveyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        var detail = Detail(
            surveyId, "Thanks!", allowAnonymous: false, publicSlug: "board-vote");
        surveys.ResolvePublicContextAsync("board-vote", userId, Arg.Any<CancellationToken>())
            .Returns(new SurveyPublicContext(
                surveyId, detail, SurveyPublicAccess.Ineligible));
        var sut = CreateController(surveys, out var session, userId);
        SurveyWizardSession.SaveBySlug(session, "board-vote", new SurveyWizardState
        {
            SurveyId = surveyId,
            UserId = userId,
            Anonymity = ResponseAnonymity.Identified,
        });

        var result = await sut.PublicPage(
            "board-vote", Xunit.TestContext.Current.CancellationToken);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        view.ViewName.Should().Be("Closed");
        view.Model.Should().BeOfType<SurveyClosedViewModel>().Subject.Reason
            .Should().Be("ineligible");
        SurveyWizardSession.LoadBySlug(session, "board-vote").Should().BeNull();
    }

    [HumansFact]
    public async Task A_page_with_nothing_visible_says_so_instead_of_claiming_the_survey_is_done()
    {
        var surveyId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        // No questions at all — the same state branching produces when it hides every one of them.
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(Detail(surveyId, thankYou: "Thanks — see you at the gate."));
        var sut = CreateController(surveys, out var session);
        SurveyWizardSession.Save(session, Token, new SurveyWizardState { SurveyId = surveyId });

        var result = await sut.Page(Token, Xunit.TestContext.Current.CancellationToken);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        view.ViewName.Should().Be("Closed");
        view.Model.Should().BeOfType<SurveyClosedViewModel>().Subject.Reason.Should().Be("empty");
        SurveyWizardSession.LoadCompleted(session, Token)
            .Should().BeNull("nothing was submitted, so nothing may mark the respondent as done");
    }

    private static SurveyController CreateController(
        ISurveyService surveys,
        out ISession session,
        Guid? userId = null)
    {
        var localizer = Substitute.For<IStringLocalizer<SurveysResource>>();
        localizer["Survey_ThankYouFallback"]
            .Returns(new LocalizedString("Survey_ThankYouFallback", "Thanks!"));
        session = new InMemorySession();
        var httpContext = new DefaultHttpContext { Session = session };
        if (userId is not null)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString())],
                "test"));
        }

        return new SurveyController(
            surveys,
            Substitute.For<IUserService>(),
            new FakeClock(Instant.FromUtc(2026, 8, 27, 9, 0)),
            new SurveyPreviewTokenProvider(DataProtectionProvider.Create("survey-thankyou-tests")),
            localizer,
            NullLogger<SurveyController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext,
            },
        };
    }

    private static SurveyDetail Detail(
        Guid surveyId,
        string thankYou,
        string? spanishThankYou = null,
        bool allowAnonymous = true,
        string? publicSlug = null,
        bool isAsociadoVote = false)
        => new(
            surveyId,
            SurveyStatus.Open,
            new SurveyEditInput(
                Text("Volunteer survey"),
                LocalizedText.Empty,
                spanishThankYou is null
                    ? Text(thankYou)
                    : new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["en"] = thankYou,
                        ["es"] = spanishThankYou,
                    }),
                LocalizedText.Empty,
                LocalizedText.Empty,
                "en",
                AllowAnonymous: allowAnonymous,
                OpensAt: null,
                ClosesAt: null,
                AudienceType: null,
                AudienceTeamId: null,
                AudienceLoggedInSince: null,
                PublicSlug: publicSlug,
                Questions: [])
            {
                IsAsociadoVote = isAsociadoVote,
            },
            Guid.NewGuid());

    private static LocalizedText Text(string en) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = en });

    /// <summary>The wizard only ever does string get/set/remove; nothing here needs a session store.</summary>
    private sealed class InMemorySession : ISession
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

        public bool IsAvailable => true;

        public string Id => "test-session";

        public IEnumerable<string> Keys => _values.Keys;

        public void Clear() => _values.Clear();

        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void Remove(string key) => _values.Remove(key);

        public void Set(string key, byte[] value) => _values[key] = value;

        public bool TryGetValue(string key, out byte[] value) => _values.TryGetValue(key, out value!);
    }
}
