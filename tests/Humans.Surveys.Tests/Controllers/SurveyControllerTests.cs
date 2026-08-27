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
        surveys.ResolvePublicContextAsync("other-survey", Arg.Any<CancellationToken>())
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

    private static SurveyController CreateController(ISurveyService surveys, out ISession session)
    {
        var localizer = Substitute.For<IStringLocalizer<SurveysResource>>();
        localizer["Survey_ThankYouFallback"]
            .Returns(new LocalizedString("Survey_ThankYouFallback", "Thanks!"));
        session = new InMemorySession();
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
                HttpContext = new DefaultHttpContext { Session = session },
            },
        };
    }

    private static SurveyDetail Detail(Guid surveyId, string thankYou, string? spanishThankYou = null)
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
                AllowAnonymous: true,
                null,
                null,
                null,
                null,
                null,
                null,
                []));

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
