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

namespace Humans.Surveys.Tests.Controllers;

/// <summary>
/// The respondent-facing wizard's closing page. Submitting flips <c>Invitation.Completed</c>, which
/// makes the invite token stop resolving — so the thank-you page cannot re-derive the survey from the
/// token and reads a completion marker left in the session at submit instead.
/// </summary>
public sealed class SurveyControllerTests
{
    private const string Token = "invite-token";

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
                HasResumableDraft: false));
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
        state!.Anonymity.Should().Be(ResponseAnonymity.Identified);
        state.UserId.Should().Be(userId);
        state.InvitationId.Should().Be(participationId);
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
            Substitute.For<IUserServiceRead>(),
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
        string? publicSlug = null)
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
                Questions: []));

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
