using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Surveys.Domain;
using Humans.Surveys.Services;
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
        var messages = Substitute.For<IEmailMessageFactory>();
        var emailPreviews = Substitute.For<IEmailPreviewServiceRead>();
        var previewTokens = new SurveyPreviewTokenProvider(
            DataProtectionProvider.Create("survey-preview-page-tests"));
        var editable = new SurveyEditInput(
            Text("Volunteer survey"), LocalizedText.Empty, LocalizedText.Empty,
            Text("Choose our gathering dates"), Text("Tell us which weekends work for you."),
            "en",
            false, null, null, null, null, null, null, []);
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Draft, editable));
        userEmails.GetNotificationTargetEmailsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [userId] = "tester@example.com" });
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserInfo(
                userId, "Tester", false, "en", null, Instant.FromUnixTimeSeconds(0),
                null, null, null, null, null, false, null, false, null, null, null,
                null, null, null, [], [], [], null, []));
        var rendered = new EmailMessage(
            "tester@example.com", "Tester", "Choose our gathering dates",
            "<h2>Volunteer survey</h2><p>Tell us which weekends work for you.</p>",
            "survey_invitation", MessageCategory.System);
        messages.SurveyInvitation(
                "tester@example.com", "Tester", "Volunteer survey", Arg.Any<string>(), "en",
                "Choose our gathering dates", "Tell us which weekends work for you.")
            .Returns(rendered);
        var wrapped = new RenderedEmailPreview(
            "tester@example.com", "Choose our gathering dates", "<html>Branded preview</html>");
        emailPreviews.RenderSystemMessage(rendered).Returns(wrapped);
        var sut = new SurveyPreviewEmailService(
            surveys, userEmails, users, emailService, messages, emailPreviews, previewTokens,
            NullLogger<SurveyPreviewEmailService>.Instance);

        var preview = await sut.PreviewForUserAsync(
            surveyId, userId, Xunit.TestContext.Current.CancellationToken);

        preview.Should().Be(wrapped);
        emailPreviews.Received(1).RenderSystemMessage(rendered);
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
        var messages = Substitute.For<IEmailMessageFactory>();
        var emailPreviews = Substitute.For<IEmailPreviewServiceRead>();
        var previewTokens = new SurveyPreviewTokenProvider(
            DataProtectionProvider.Create("survey-preview-email-tests"));
        var editable = new SurveyEditInput(
            Text("Volunteer survey"), LocalizedText.Empty, LocalizedText.Empty,
            Text("Choose our gathering dates"), Text("Tell us which weekends work for you."),
            "en",
            false, null, null, null, null, null, null, []);
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Draft, editable));
        userEmails.GetNotificationTargetEmailsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Single() == userId),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [userId] = "tester@example.com" });
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserInfo(
                userId, "Tester", false, "fr", null, Instant.FromUnixTimeSeconds(0),
                null, null, null, null, null, false, null, false, null, null, null,
                null, null, null, [], [], [], null, []));
        string? capturedToken = null;
        var rendered = new EmailMessage(
            "tester@example.com", string.Empty, "Survey", "<p>Body</p>",
            "survey_invitation", MessageCategory.System);
        messages.SurveyInvitation(
                "tester@example.com",
                "Tester",
                "Volunteer survey",
                Arg.Do<string>(token => capturedToken = token),
                "fr",
                "Choose our gathering dates",
                "Tell us which weekends work for you.")
            .Returns(rendered);
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
        capturedToken.Should().NotBeNull();
        previewTokens.Resolve(capturedToken!).Should().Be(new SurveyPreviewLink(surveyId, "fr"));
        await emailService.Received(1).SendAsync(rendered, Arg.Any<CancellationToken>());
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
        var messages = Substitute.For<IEmailMessageFactory>();
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
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Draft, editable));
        userEmails.GetNotificationTargetEmailsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [userId] = "tester@example.com" });
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserInfo(
                userId, "Tester", false, "fr", null, Instant.FromUnixTimeSeconds(0),
                null, null, null, null, null, false, null, false, null, null, null,
                null, null, null, [], [], [], null, []));
        var rendered = new EmailMessage(
            "tester@example.com", "Tester", "Standard French subject", "<p>Standard French body</p>",
            "survey_invitation", MessageCategory.System);
        messages.SurveyInvitation(
                "tester@example.com", "Tester", "Volunteer survey", Arg.Any<string>(), "fr",
                string.Empty, string.Empty)
            .Returns(rendered);
        emailPreviews.RenderSystemMessage(rendered)
            .Returns(new RenderedEmailPreview(
                "tester@example.com", rendered.Subject, rendered.HtmlBody));
        var sut = new SurveyPreviewEmailService(
            surveys, userEmails, users, emailService, messages, emailPreviews, previewTokens,
            NullLogger<SurveyPreviewEmailService>.Instance);

        await sut.PreviewForUserAsync(
            surveyId, userId, Xunit.TestContext.Current.CancellationToken);

        messages.Received(1).SurveyInvitation(
            "tester@example.com", "Tester", "Volunteer survey", Arg.Any<string>(), "fr",
            string.Empty, string.Empty);
    }

    private static LocalizedText Text(string en) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = en });
}
