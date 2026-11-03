using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Configuration;
using Humans.Email.Controllers;
using Humans.Email.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Email.Tests;

/// <summary>
/// Pause/resume post from the /Settings#email tab, so both
/// actions send the admin back there rather than to the /Email/EmailOutbox dashboard.
/// </summary>
public sealed class EmailControllerPauseSettingsRedirectTests
{
    private readonly Guid _actorId = Guid.NewGuid();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IEmailOutboxManagement _outbox = Substitute.For<IEmailOutboxManagement>();

    public EmailControllerPauseSettingsRedirectTests()
    {
        _users.GetUserInfoAsync(_actorId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = _actorId }, [], [], profile: null));
    }

    private EmailController BuildSut()
    {
        var controller = new EmailController(
            _users, _outbox, NullLogger<EmailController>.Instance, Options.Create(new EmailSettings()));
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, _actorId.ToString())], "test")),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        return controller;
    }

    [HumansTheory]
    [Xunit.InlineData(0, "merged")]
    [Xunit.InlineData(0, "missing")]
    [Xunit.InlineData(0, "cancelled")]
    [Xunit.InlineData(1, "merged")]
    [Xunit.InlineData(1, "missing")]
    [Xunit.InlineData(1, "cancelled")]
    [Xunit.InlineData(2, "merged")]
    [Xunit.InlineData(2, "missing")]
    [Xunit.InlineData(2, "cancelled")]
    [Xunit.InlineData(3, "merged")]
    [Xunit.InlineData(3, "missing")]
    [Xunit.InlineData(3, "cancelled")]
    [Xunit.InlineData(4, "merged")]
    [Xunit.InlineData(4, "missing")]
    [Xunit.InlineData(4, "cancelled")]
    public async Task AdminMutations_ResolveTheActorBeforeWriting(int operation, string account)
    {
        using var request = new CancellationTokenSource();
        var survivorId = Guid.NewGuid();
        async ValueTask<UserInfo?> ReadAccountAsync(CancellationToken ct)
        {
            if (string.Equals(account, "cancelled", StringComparison.Ordinal))
            {
                await request.CancelAsync();
                ct.ThrowIfCancellationRequested();
            }
            return string.Equals(account, "missing", StringComparison.Ordinal)
                ? null : UserInfo.Create(new User { Id = survivorId }, [], [], profile: null);
        }
        _users.GetUserInfoAsync(_actorId, Arg.Any<CancellationToken>())
            .Returns(call => ReadAccountAsync(call.Arg<CancellationToken>()));
        var id = Guid.NewGuid();
        _outbox.RetryMessageAsync(id, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("member@example.com");
        _outbox.DiscardMessageAsync(id, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("member@example.com");
        var controller = BuildSut();
        controller.HttpContext.RequestAborted = request.Token;
        Task<IActionResult> MutateAsync() => operation switch
        {
            0 => controller.BackfillDailyCounts(),
            1 => controller.PauseEmailSending(),
            2 => controller.ResumeEmailSending(),
            3 => controller.RetryEmailOutboxMessage(id),
            _ => controller.DiscardEmailOutboxMessage(id),
        };

        if (string.Equals(account, "cancelled", StringComparison.Ordinal))
        {
            Func<Task> mutation = MutateAsync;
            await mutation.Should().ThrowAsync<OperationCanceledException>();
            _outbox.ReceivedCalls().Should().BeEmpty();
        }
        else if (string.Equals(account, "missing", StringComparison.Ordinal))
        {
            (await MutateAsync()).Should().BeOfType<UnauthorizedResult>();
            _outbox.ReceivedCalls().Should().BeEmpty();
        }
        else
        {
            var result = await MutateAsync();
            if (operation is 1 or 2)
                result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#email");
            else
                result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be(nameof(EmailController.EmailOutbox));
            switch (operation)
            {
                case 0:
                    await _outbox.Received(1).BackfillDailySendCountsAsync(survivorId, CancellationToken.None);
                    break;
                case 1:
                case 2:
                    await _outbox.Received(1).SetEmailPausedAsync(operation == 1, survivorId, CancellationToken.None);
                    break;
                case 3:
                    await _outbox.Received(1).RetryMessageAsync(id, survivorId, CancellationToken.None);
                    break;
                default:
                    await _outbox.Received(1).DiscardMessageAsync(id, survivorId, CancellationToken.None);
                    break;
            }
        }
        await _users.Received(1).GetUserInfoAsync(_actorId, request.Token);
    }

    [HumansFact]
    public async Task PauseEmailSending_RedirectsToTheSettingsPageEmailTab()
    {
        var result = await BuildSut().PauseEmailSending();

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#email");
    }

    [HumansFact]
    public async Task ResumeEmailSending_RedirectsToTheSettingsPageEmailTab()
    {
        var result = await BuildSut().ResumeEmailSending();

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#email");
    }
}
