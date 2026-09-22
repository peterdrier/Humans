using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Email.Controllers;
using Humans.Email.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Email.Tests.Controllers;

/// <summary>
/// The shared composer's "Send to me" (peterdrier/Humans#1793): one branded System email to the
/// signed-in human's own address, audited, never to anyone else; refused when signed out.
/// </summary>
public sealed class ComposerSelfSendTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly IUserEmailService _userEmails = Substitute.For<IUserEmailService>();
    private readonly IEmailService _email = Substitute.For<IEmailService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();

    private ComposerSelfSendService BuildService()
    {
        var localizer = Substitute.For<IStringLocalizer<EmailResource>>();
        localizer["Email_ComposerSelfTest_Subject"].Returns(new LocalizedString("s", "[Test] {0}"));
        localizer["Email_ComposerSelfTest_DefaultSubject"].Returns(new LocalizedString("d", "[Test] Preview"));
        return new ComposerSelfSendService(
            _userEmails, Substitute.For<IUserServiceRead>(), _email, _audit, localizer,
            NullLogger<ComposerSelfSendService>.Instance);
    }

    private EmailPreviewController BuildController(bool signedIn)
    {
        var controller = new EmailPreviewController(
            Substitute.For<IUserServiceRead>(), Substitute.For<IEmailPreviewService>(), BuildService());
        var claims = signedIn ? new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) } : [];
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
        };
        return controller;
    }

    private void GivenAddress(string address) =>
        _userEmails.GetNotificationTargetEmailsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == UserId), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [UserId] = address });

    [HumansFact]
    public async Task SendMarkdownToSelf_QueuesOneSystemEmailToTheCallerAndAudits()
    {
        GivenAddress("me@example.com");

        var result = await BuildController(signedIn: true)
            .SendMarkdownToSelf("Hello", "**Hi** there", CancellationToken.None);

        result.Should().BeOfType<JsonResult>();
        await _email.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m =>
                m.RecipientEmail == "me@example.com"
                && m.UserId == UserId
                && m.Subject == "[Test] Hello"
                && m.HtmlBody.Contains("<strong>Hi</strong>")
                && m.TemplateName == ComposerSelfSendService.TemplateName
                && m.Category == MessageCategory.System),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.EmailComposerSelfTestSent, Arg.Any<string>(), UserId, Arg.Any<string>(), UserId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task SendMarkdownToSelf_WithoutSubject_UsesTheDefaultTestSubject()
    {
        GivenAddress("me@example.com");

        await BuildController(signedIn: true).SendMarkdownToSelf(null, "Body", CancellationToken.None);

        await _email.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.Subject == "[Test] Preview"), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendMarkdownToSelf_WhenSignedOut_IsUnauthorizedAndSendsNothing()
    {
        var result = await BuildController(signedIn: false)
            .SendMarkdownToSelf("Hello", "Body", CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
        await _email.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default, default!, default, default!, default(Guid));
    }

    [HumansFact]
    public async Task SendMarkdownToSelf_WithNoAddress_Is422AndSendsNothing()
    {
        _userEmails.GetNotificationTargetEmailsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>());

        var result = await BuildController(signedIn: true)
            .SendMarkdownToSelf("Hello", "Body", CancellationToken.None);

        result.Should().BeOfType<UnprocessableEntityResult>();
        await _email.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
    }

    [HumansFact]
    public void SendMarkdownToSelf_RequiresAuthenticationAndIsRateLimited()
    {
        typeof(EmailPreviewController).GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull();
        typeof(EmailPreviewController).GetMethod(nameof(EmailPreviewController.SendMarkdownToSelf))!
            .GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName
            .Should().Be(EmailPreviewController.SelfSendRateLimitPolicy);
    }
}
