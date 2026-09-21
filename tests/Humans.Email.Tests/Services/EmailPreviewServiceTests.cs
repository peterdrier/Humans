using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Email.Services;
using Humans.Users.Contracts;
using NSubstitute;

namespace Humans.Email.Tests.Services;

public sealed class EmailPreviewServiceTests
{
    [HumansFact]
    public void RenderSystemMessage_uses_the_canonical_body_composer()
    {
        var composer = Substitute.For<IEmailBodyComposer>();
        composer.Compose("<p>Invitation</p>").Returns(("<html>Branded invitation</html>", "Invitation"));
        var sut = new EmailPreviewService(composer);
        var message = new EmailMessage(
            "tester@example.com", "Tester", "Survey subject", "<p>Invitation</p>",
            "survey_invitation", MessageCategory.System);

        var preview = sut.RenderSystemMessage(message);

        preview.Should().Be(new RenderedEmailPreview(
            "tester@example.com", "Survey subject", "<html>Branded invitation</html>"));
        composer.Received(1).Compose("<p>Invitation</p>");
    }

    [HumansFact]
    public void RenderSystemMessage_rejects_opt_outable_messages()
    {
        var composer = Substitute.For<IEmailBodyComposer>();
        var sut = new EmailPreviewService(composer);
        var message = new EmailMessage(
            "tester@example.com", "Tester", "Team update", "<p>Update</p>",
            "added_to_team", MessageCategory.TeamUpdates);

        var action = () => sut.RenderSystemMessage(message);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*recipient-specific send policy*");
        composer.DidNotReceive().Compose(Arg.Any<string>(), Arg.Any<string?>());
    }

    [HumansFact]
    public void RenderMarkdown_always_on_category_has_no_unsubscribe_placeholder()
    {
        var composer = Substitute.For<IEmailBodyComposer>();
        composer.Compose(Arg.Any<string>(), Arg.Any<string?>()).Returns(("<html>Branded</html>", "Plain"));
        var sut = new EmailPreviewService(composer);

        var preview = sut.RenderMarkdown("Subject", "**Hello**", MessageCategory.System);

        preview.Should().Be(new RenderedEmailPreview(string.Empty, "Subject", "<html>Branded</html>"));
        composer.Received(1).Compose(Arg.Is<string>(h => h.Contains("<strong>Hello</strong>")), null);
    }

    [HumansFact]
    public void RenderMarkdown_opt_outable_category_gets_placeholder_unsubscribe_footer()
    {
        var composer = Substitute.For<IEmailBodyComposer>();
        composer.Compose(Arg.Any<string>(), Arg.Any<string?>()).Returns(("<html>Branded</html>", "Plain"));
        var sut = new EmailPreviewService(composer);

        sut.RenderMarkdown("Subject", "Hi", MessageCategory.FacilitatedMessages);

        composer.Received(1).Compose(Arg.Any<string>(), "#");
    }

    [HumansFact]
    public void RenderMarkdown_no_category_has_no_unsubscribe_placeholder()
    {
        var composer = Substitute.For<IEmailBodyComposer>();
        composer.Compose(Arg.Any<string>(), Arg.Any<string?>()).Returns(("<html>Branded</html>", "Plain"));
        var sut = new EmailPreviewService(composer);

        sut.RenderMarkdown("Subject", "Hi", null);

        composer.Received(1).Compose(Arg.Any<string>(), null);
    }
}
