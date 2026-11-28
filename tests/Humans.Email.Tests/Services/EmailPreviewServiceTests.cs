using System.Globalization;
using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Email.Services;
using NSubstitute;

namespace Humans.Email.Tests.Services;

public sealed class EmailPreviewServiceTests
{
    [HumansFact]
    public void RenderSystemMessage_Body_UsesCanonicalComposer()
    {
        var composer = Substitute.For<IEmailBodyComposer>();
        composer.Compose("<p>Invitation</p>").Returns(("<html>Branded invitation</html>", "Invitation"));
        var sut = new EmailPreviewService(composer);
        var message = new EmailMessage(
            "tester@example.com", "Tester", Guid.NewGuid(), "Survey subject", "<p>Invitation</p>",
            "survey_invitation", MessageCategory.System);

        var preview = sut.RenderSystemMessage(message);

        preview.Should().Be(new RenderedEmailPreview(
            "tester@example.com", "Survey subject", "<html>Branded invitation</html>"));
        composer.Received(1).Compose("<p>Invitation</p>");
    }

    [HumansFact]
    public void RenderSystemMessage_OptOutableMessage_IsRejected()
    {
        var composer = Substitute.For<IEmailBodyComposer>();
        var sut = new EmailPreviewService(composer);
        var message = new EmailMessage(
            "tester@example.com", "Tester", Guid.NewGuid(), "Team update", "<p>Update</p>",
            "added_to_team", MessageCategory.TeamUpdates);

        var action = () => sut.RenderSystemMessage(message);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*recipient-specific send policy*");
        composer.DidNotReceive().Compose(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public void RenderMarkdown_AlwaysOnCategory_HasNoUnsubscribe()
    {
        var composer = Substitute.For<IEmailBodyComposer>();
        composer.Compose(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(("<html>Branded</html>", "Plain"));
        var sut = new EmailPreviewService(composer);

        var preview = sut.RenderMarkdown("Subject", "**Hello**", MessageCategory.System);

        preview.Should().Be(new RenderedEmailPreview(string.Empty, "Subject", "<html>Branded</html>"));
        composer.Received(1).Compose(Arg.Is<string>(h => h.Contains("<strong>Hello</strong>")), null, CultureInfo.CurrentUICulture.Name);
    }

    [HumansFact]
    public void RenderMarkdown_OptOutableCategory_GetsUnsubscribeFooter()
    {
        var composer = Substitute.For<IEmailBodyComposer>();
        composer.Compose(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(("<html>Branded</html>", "Plain"));
        var sut = new EmailPreviewService(composer);

        sut.RenderMarkdown("Subject", "Hi", MessageCategory.FacilitatedMessages);

        composer.Received(1).Compose(Arg.Any<string>(), "#", CultureInfo.CurrentUICulture.Name);
    }

    [HumansFact]
    public void RenderMarkdown_NoCategory_HasNoUnsubscribe()
    {
        var composer = Substitute.For<IEmailBodyComposer>();
        composer.Compose(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(("<html>Branded</html>", "Plain"));
        var sut = new EmailPreviewService(composer);

        sut.RenderMarkdown("Subject", "Hi");

        composer.Received(1).Compose(Arg.Any<string>(), null, CultureInfo.CurrentUICulture.Name);
    }
    [HumansFact]
    public void Compose_MarkdownTable_KeepsCellsInPlainText()
    {
        var environment = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        environment.EnvironmentName.Returns("Production");
        var composer = new BrandedEmailBodyComposer(
            Microsoft.Extensions.Options.Options.Create(new Base.Configuration.EmailSettings()), environment);
        var html = Base.Extensions.SanitizedMarkdownRenderer.Render(
            "| Name | Shift |\n| --- | --- |\n| Alice | [Gate](https://humans.example/shift) |");

        var body = composer.Compose(html);

        body.HtmlBody.Should().Contain("<table");
        body.PlainTextBody.Should().Be("Name\tShift\nAlice\tGate (https://humans.example/shift)");
    }

    [HumansFact]
    public void RenderMarkdown_UsesTheCallersCultureForThePlaceholderFooter()
    {
        var callerCulture = Base.Extensions.CultureCatalog.CanonicalLegalCultureCode;
        using var culture = new Base.Extensions.CultureScope(callerCulture);
        var environment = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        environment.EnvironmentName.Returns("Production");
        var composer = new BrandedEmailBodyComposer(
            Microsoft.Extensions.Options.Options.Create(new Base.Configuration.EmailSettings()), environment);

        var preview = new EmailPreviewService(composer).RenderMarkdown("Subject", "Hi", MessageCategory.Governance);

        var resources = new System.Resources.ResourceManager(typeof(EmailResource));
        preview.HtmlBody.Should().Contain(resources.GetString("Email_UnsubscribeFooter", CultureInfo.GetCultureInfo(callerCulture)))
            .And.NotContain(resources.GetString("Email_UnsubscribeFooter", CultureInfo.InvariantCulture));
        preview.HtmlBody.Should().Contain("href=\"#\"");
    }

}
