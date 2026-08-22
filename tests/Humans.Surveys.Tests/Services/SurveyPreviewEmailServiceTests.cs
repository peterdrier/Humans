using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Surveys.Domain;
using Humans.Surveys.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Surveys.Tests.Services;

public sealed class SurveyPreviewEmailServiceTests
{
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
        var previewTokens = new SurveyPreviewTokenProvider(
            DataProtectionProvider.Create("survey-preview-email-tests"));
        var editable = new SurveyEditInput(
            Text("Volunteer survey"), LocalizedText.Empty, LocalizedText.Empty, "en",
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
                "fr")
            .Returns(rendered);
        var sut = new SurveyPreviewEmailService(
            surveys,
            userEmails,
            users,
            emailService,
            messages,
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

    private static LocalizedText Text(string en) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = en });
}
