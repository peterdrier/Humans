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
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Humans.Email.Tests.Controllers;

/// <summary>
/// The shared composer's "Send to me": one branded System email to the
/// signed-in human's own address, audited, never to anyone else; refused when signed out.
/// </summary>
public sealed class ComposerSelfSendTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IEmailService _email = Substitute.For<IEmailService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly ILogger<ComposerSelfSendService> _logger = Substitute.For<ILogger<ComposerSelfSendService>>();

    private ComposerSelfSendService BuildService()
    {
        var localizer = Substitute.For<IStringLocalizer<EmailResource>>();
        localizer["Email_ComposerSelfTest_Subject"].Returns(new LocalizedString("s", "[Test] {0}"));
        localizer["Email_ComposerSelfTest_DefaultSubject"].Returns(new LocalizedString("d", "[Test] Preview"));
        return new ComposerSelfSendService(
            _users, _email, _audit, localizer,
            _logger);
    }

    private EmailPreviewController BuildController(bool signedIn)
    {
        var controller = new EmailPreviewController(
            Substitute.For<IUserService>(), Substitute.For<IEmailPreviewManagement>(), BuildService());
        var claims = signedIn ? new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) } : [];
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
        };
        return controller;
    }

    private void GivenAddress(string? address, Guid? resolvedId = null) =>
        _users.GetUserInfoAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = resolvedId ?? UserId, Email = address }, [], [], null));

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task SendMarkdownToSelf_QueuesOneSystemEmailToTheCallerAndAudits(bool mergedAlias)
    {
        var resolvedId = mergedAlias ? Guid.NewGuid() : UserId;
        GivenAddress("me@example.com", resolvedId);

        var result = await BuildController(signedIn: true)
            .SendMarkdownToSelf("Hello", "**Hi** there", CancellationToken.None);

        result.Should().BeOfType<JsonResult>();
        await _email.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m =>
                m.RecipientEmail == "me@example.com"
                && m.UserId == resolvedId
                && m.Subject == "[Test] Hello"
                && m.HtmlBody.Contains("<strong>Hi</strong>")
                && m.TemplateName == ComposerSelfSendService.TemplateName
                && m.Category == MessageCategory.System),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            EmailAuditActions.EmailComposerSelfTestSent, Arg.Any<string>(), resolvedId, Arg.Any<string>(), resolvedId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task SendMarkdownToSelf_CancelsOnlyTheAccountRead(bool abortDuringSend)
    {
        using var request = new CancellationTokenSource();
        async ValueTask<UserInfo?> ReadAccountAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return UserInfo.Create(new User { Id = UserId, Email = "me@example.com" }, [], [], null);
        }
        _users.GetUserInfoAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(call => ReadAccountAsync(call.Arg<CancellationToken>()));
        async Task SendAsync(CancellationToken ct)
        {
            await request.CancelAsync();
            ct.ThrowIfCancellationRequested();
        }
        _email.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .Returns(call => SendAsync(call.Arg<CancellationToken>()));
        var controller = BuildController(signedIn: true);
        controller.HttpContext.RequestAborted = request.Token;
        if (abortDuringSend)
        {
            (await controller.SendMarkdownToSelf("Hello", "Body", request.Token))
                .Should().BeOfType<JsonResult>();
            await _email.Received(1).SendAsync(Arg.Any<EmailMessage>(), CancellationToken.None);
            await _audit.Received(1).LogAsync(EmailAuditActions.EmailComposerSelfTestSent,
                "User", UserId, Arg.Any<string>(), UserId, Arg.Any<Guid?>(), Arg.Any<string?>());
        }
        else
        {
            await request.CancelAsync();
            var send = () => controller.SendMarkdownToSelf("Hello", "Body", request.Token);
            await send.Should().ThrowAsync<OperationCanceledException>();
            _email.ReceivedCalls().Should().BeEmpty();
            _audit.ReceivedCalls().Should().BeEmpty();
        }
        await _users.Received(1).GetUserInfoAsync(UserId, request.Token);
    }

    [HumansFact]
    public async Task SendMarkdownToSelf_AuditFailureAfterQueueing_KeepsSuccessAndLogsCritical()
    {
        GivenAddress("me@example.com");
        var failure = new InvalidOperationException("Audit unavailable");
        _audit.LogAsync(Arg.Any<AuditActionDefinition>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>())
            .Returns(Task.FromException(failure));

        var result = await BuildController(signedIn: true)
            .SendMarkdownToSelf("Private subject", "Private message", CancellationToken.None);

        result.Should().BeOfType<JsonResult>().Which.Value.Should().BeEquivalentTo(new { email = "me@example.com" });
        await _email.Received(1).SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        var log = _logger.ReceivedCalls()
            .Single(call => string.Equals(call.GetMethodInfo().Name, "Log", StringComparison.Ordinal)
                && Equals(call.GetArguments()[0], LogLevel.Critical))
            .GetArguments();
        log[3].Should().BeSameAs(failure);
        log[2]!.ToString().Should().Contain(UserId.ToString())
            .And.NotContain("Private subject").And.NotContain("Private message").And.NotContain("me@example.com");
    }

    [HumansFact]
    public async Task SendMarkdownToSelf_QueueFailure_PropagatesWithoutSuccessAudit()
    {
        GivenAddress("me@example.com");
        var failure = new InvalidOperationException("Queue unavailable");
        _email.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(failure));

        var send = () => BuildController(signedIn: true)
            .SendMarkdownToSelf("Hello", "Body", CancellationToken.None);

        (await send.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(null!, default!, default, default!, default(Guid));
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
        await _email.DidNotReceiveWithAnyArgs().SendAsync(default!);
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(null!, default!, default, default!, default(Guid));
    }

    [HumansFact]
    public async Task SendMarkdownToSelf_WithNoAddress_Is422AndSendsNothing()
    {
        GivenAddress(null);

        var result = await BuildController(signedIn: true)
            .SendMarkdownToSelf("Hello", "Body", CancellationToken.None);

        result.Should().BeOfType<UnprocessableEntityResult>();
        await _email.DidNotReceiveWithAnyArgs().SendAsync(default!);
    }

    [HumansFact]
    public void SendMarkdownToSelf_RequiresAuthenticationAndIsRateLimited()
    {
        typeof(EmailPreviewController).GetCustomAttribute<AuthorizeAttribute>(inherit: false).Should().NotBeNull();
        typeof(EmailPreviewController).GetMethod(nameof(EmailPreviewController.SendMarkdownToSelf))!
            .GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName
            .Should().Be(EmailPreviewController.SelfSendRateLimitPolicy);
    }
}
