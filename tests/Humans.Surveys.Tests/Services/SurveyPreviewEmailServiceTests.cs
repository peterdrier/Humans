using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Surveys.Domain;
using Humans.Surveys.Services;
using Humans.Surveys.Tests.Infrastructure;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Humans.Surveys.Contracts;

namespace Humans.Surveys.Tests.Services;

public sealed class SurveyPreviewEmailServiceTests
{
    [HumansFact]
    public async Task PreviewForUserAsync_returns_the_rendered_message_without_sending_it()
    {
        var surveyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        var userEmails = Substitute.For<IUserEmailService>();
        var users = Substitute.For<IUserServiceRead>();
        var emailService = Substitute.For<IEmailService>();
        var messages = TestSurveysEmails.Create();
        var emailPreviews = Substitute.For<IEmailPreviewServiceRead>();
        var previewTokens = new SurveyPreviewTokenProvider(
            DataProtectionProvider.Create("survey-preview-page-tests"));
        var editable = new SurveyEditInput(
            Text("Volunteer survey"), LocalizedText.Empty, LocalizedText.Empty,
            Text("Choose our gathering dates"), Text("Tell us which weekends work for you."),
            "en",
            false, null, null, null, null, null, null, []);
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Draft, editable, Guid.NewGuid()));
        userEmails.GetNotificationTargetEmailsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [userId] = "tester@example.com" });
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserInfo(
                userId, "Tester", false, "en", null, Instant.FromUnixTimeSeconds(0),
                null, null, null, null, null, false, false, null, null, null,
                null, null, null, [], [], [], null, []));
        var wrapped = new RenderedEmailPreview(
            "tester@example.com", "Choose our gathering dates", "<html>Branded preview</html>");
        emailPreviews.RenderSystemMessage(Arg.Any<EmailMessage>()).Returns(wrapped);
        var sut = new SurveyPreviewEmailService(
            surveys, userEmails, users, emailService, messages, emailPreviews, previewTokens,
            NullLogger<SurveyPreviewEmailService>.Instance);

        var preview = await sut.PreviewForUserAsync(
            surveyId, userId, Xunit.TestContext.Current.CancellationToken);

        preview.Should().Be(wrapped);
        emailPreviews.Received(1).RenderSystemMessage(Arg.Is<EmailMessage>(m =>
            m.TemplateName == "survey_invitation"
            && m.RecipientEmail == "tester@example.com"
            && m.Subject == "Choose our gathering dates"
            && m.HtmlBody.Contains("Tell us which weekends work for you.", StringComparison.Ordinal)));
        await emailService.DidNotReceive().SendAsync(
            Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendToUserAsync_uses_invitation_template_without_creating_an_invitation()
    {
        var surveyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        var userEmails = Substitute.For<IUserEmailService>();
        var users = Substitute.For<IUserServiceRead>();
        var emailService = Substitute.For<IEmailService>();
        var messages = TestSurveysEmails.Create();
        var emailPreviews = Substitute.For<IEmailPreviewServiceRead>();
        var previewTokens = new SurveyPreviewTokenProvider(
            DataProtectionProvider.Create("survey-preview-email-tests"));
        var editable = new SurveyEditInput(
            Text("Volunteer survey"), LocalizedText.Empty, LocalizedText.Empty,
            Text("Choose our gathering dates"), Text("Tell us which weekends work for you."),
            "en",
            false, null, null, null, null, null, null, []);
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Draft, editable, Guid.NewGuid()));
        userEmails.GetNotificationTargetEmailsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Single() == userId),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [userId] = "tester@example.com" });
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserInfo(
                userId, "Tester", false, "fr", null, Instant.FromUnixTimeSeconds(0),
                null, null, null, null, null, false, false, null, null, null,
                null, null, null, [], [], [], null, []));
        var sut = new SurveyPreviewEmailService(
            surveys,
            userEmails,
            users,
            emailService,
            messages,
            emailPreviews,
            previewTokens,
            NullLogger<SurveyPreviewEmailService>.Instance);

        var destination = await sut.SendToUserAsync(
            surveyId, userId, Xunit.TestContext.Current.CancellationToken);

        destination.Should().Be("tester@example.com");
        // The token now travels inside the built body's answer link, so read it back from there.
        var sent = emailService.ReceivedCalls()
            .Single(c => string.Equals(c.GetMethodInfo().Name, nameof(IEmailService.SendAsync), StringComparison.Ordinal))
            .GetArguments()[0] as EmailMessage;
        sent!.TemplateName.Should().Be("survey_invitation");
        sent.Subject.Should().Be("Choose our gathering dates");
        var capturedToken = Uri.UnescapeDataString(AnswerToken(sent.HtmlBody));
        capturedToken.Should().NotBeEmpty();
        previewTokens.Resolve(capturedToken).Should().Be(new SurveyPreviewLink(surveyId, "fr"));
        await surveys.Received(1).GetForEditAsync(surveyId, Arg.Any<CancellationToken>());
        await surveys.DidNotReceive().SendInvitesAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await surveys.DidNotReceive().AdvanceWizardAsync(
            Arg.Any<SurveyWizardState>(), Arg.Any<int>(), Arg.Any<bool>(),
            Arg.Any<IReadOnlyList<SurveyAnswerInput>>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task PreviewForUserAsync_leaves_optional_copy_blank_when_only_an_unrelated_culture_exists()
    {
        var surveyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        var userEmails = Substitute.For<IUserEmailService>();
        var users = Substitute.For<IUserServiceRead>();
        var emailService = Substitute.For<IEmailService>();
        var messages = TestSurveysEmails.Create();
        var emailPreviews = Substitute.For<IEmailPreviewServiceRead>();
        var previewTokens = new SurveyPreviewTokenProvider(
            DataProtectionProvider.Create("survey-preview-language-fallback-tests"));
        var spanishOnlySubject = new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["en"] = string.Empty,
            ["es"] = "Elige una fecha",
        });
        var spanishOnlyMessage = new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["en"] = string.Empty,
            ["es"] = "Dinos qué te conviene.",
        });
        var editable = new SurveyEditInput(
            Text("Volunteer survey"), LocalizedText.Empty, LocalizedText.Empty,
            spanishOnlySubject, spanishOnlyMessage, "en",
            false, null, null, null, null, null, null, []);
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Draft, editable, Guid.NewGuid()));
        userEmails.GetNotificationTargetEmailsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [userId] = "tester@example.com" });
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserInfo(
                userId, "Tester", false, "fr", null, Instant.FromUnixTimeSeconds(0),
                null, null, null, null, null, false, false, null, null, null,
                null, null, null, [], [], [], null, []));
        emailPreviews.RenderSystemMessage(Arg.Any<EmailMessage>())
            .Returns(call => new RenderedEmailPreview(
                "tester@example.com", call.Arg<EmailMessage>().Subject, call.Arg<EmailMessage>().HtmlBody));
        var sut = new SurveyPreviewEmailService(
            surveys, userEmails, users, emailService, messages, emailPreviews, previewTokens,
            NullLogger<SurveyPreviewEmailService>.Instance);

        await sut.PreviewForUserAsync(
            surveyId, userId, Xunit.TestContext.Current.CancellationToken);

        // The survey's copy exists only in es, so the fr preview falls back to the
        // standard localized wording rather than borrowing the Spanish subject.
        emailPreviews.Received(1).RenderSystemMessage(Arg.Is<EmailMessage>(m =>
            m.Subject == "Please complete: Volunteer survey"));
    }

    /// <summary>The <c>t=</c> value out of the answer link the builder wrote into the body.</summary>
    private static string AnswerToken(string htmlBody)
    {
        const string marker = "/Survey/Answer?t=";
        var start = htmlBody.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = htmlBody.IndexOf('"', start);
        return htmlBody[start..end];
    }

    private static LocalizedText Text(string en) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = en });
}
