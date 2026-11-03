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

    public enum EmailOperation
    {
        BackfillDailyCounts,
        PauseSending,
        ResumeSending,
        RetryOutboxMessage,
        DiscardOutboxMessage
    }

    [HumansTheory]
    [Xunit.InlineData(EmailOperation.BackfillDailyCounts, AccountScenario.Merged)]
    [Xunit.InlineData(EmailOperation.BackfillDailyCounts, AccountScenario.Missing)]
    [Xunit.InlineData(EmailOperation.BackfillDailyCounts, AccountScenario.Cancelled)]
    [Xunit.InlineData(EmailOperation.PauseSending, AccountScenario.Merged)]
    [Xunit.InlineData(EmailOperation.PauseSending, AccountScenario.Missing)]
    [Xunit.InlineData(EmailOperation.PauseSending, AccountScenario.Cancelled)]
    [Xunit.InlineData(EmailOperation.ResumeSending, AccountScenario.Merged)]
    [Xunit.InlineData(EmailOperation.ResumeSending, AccountScenario.Missing)]
    [Xunit.InlineData(EmailOperation.ResumeSending, AccountScenario.Cancelled)]
    [Xunit.InlineData(EmailOperation.RetryOutboxMessage, AccountScenario.Merged)]
    [Xunit.InlineData(EmailOperation.RetryOutboxMessage, AccountScenario.Missing)]
    [Xunit.InlineData(EmailOperation.RetryOutboxMessage, AccountScenario.Cancelled)]
    [Xunit.InlineData(EmailOperation.DiscardOutboxMessage, AccountScenario.Merged)]
    [Xunit.InlineData(EmailOperation.DiscardOutboxMessage, AccountScenario.Missing)]
    [Xunit.InlineData(EmailOperation.DiscardOutboxMessage, AccountScenario.Cancelled)]
    public async Task AdminMutations_ResolveTheActorBeforeWriting(EmailOperation operation, AccountScenario account)
    {
        using var request = new CancellationTokenSource();
        var survivorId = Guid.NewGuid();
        async ValueTask<UserInfo?> ReadAccountAsync(CancellationToken ct)
        {
            if (account == AccountScenario.Cancelled)
            {
                await request.CancelAsync();
                ct.ThrowIfCancellationRequested();
            }
            return account == AccountScenario.Missing
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
            EmailOperation.BackfillDailyCounts => controller.BackfillDailyCounts(),
            EmailOperation.PauseSending => controller.PauseEmailSending(),
            EmailOperation.ResumeSending => controller.ResumeEmailSending(),
            EmailOperation.RetryOutboxMessage => controller.RetryEmailOutboxMessage(id),
            _ => controller.DiscardEmailOutboxMessage(id),
        };

        if (account == AccountScenario.Cancelled)
        {
            Func<Task> mutation = MutateAsync;
            await mutation.Should().ThrowAsync<OperationCanceledException>();
            _outbox.ReceivedCalls().Should().BeEmpty();
        }
        else if (account == AccountScenario.Missing)
        {
            (await MutateAsync()).Should().BeOfType<UnauthorizedResult>();
            _outbox.ReceivedCalls().Should().BeEmpty();
        }
        else
        {
            var result = await MutateAsync();
            if (operation is EmailOperation.PauseSending or EmailOperation.ResumeSending)
                result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#email");
            else
                result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be(nameof(EmailController.EmailOutbox));
            switch (operation)
            {
                case EmailOperation.BackfillDailyCounts:
                    await _outbox.Received(1).BackfillDailySendCountsAsync(survivorId, CancellationToken.None);
                    break;
                case EmailOperation.PauseSending:
                case EmailOperation.ResumeSending:
                    await _outbox.Received(1).SetEmailPausedAsync(operation == EmailOperation.PauseSending, survivorId, CancellationToken.None);
                    break;
                case EmailOperation.RetryOutboxMessage:
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
