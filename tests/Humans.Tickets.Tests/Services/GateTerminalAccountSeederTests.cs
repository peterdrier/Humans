using System.Security.Claims;
using Humans.Tickets.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Humans.AuditLog.Contracts;
using Humans.Base.Constants;
using Humans.Onboarding.Contracts;
using Humans.Tickets.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Tickets.Tests.Services;

public sealed class GateTerminalAccountSeederTests : TicketsTestHarness
{
    private readonly IUserSignInService _signIn = Substitute.For<IUserSignInService>();
    private readonly IUserService _accounts = Substitute.For<IUserService>();
    private readonly IAccountProvisioningService _provisioning = Substitute.For<IAccountProvisioningService>();
    private readonly IProfileEditorService _profiles = Substitute.For<IProfileEditorService>();
    private readonly IUserWrite _userWrite = Substitute.For<IUserWrite>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private bool _accountExists;
    private string? _password;

    public GateTerminalAccountSeederTests()
    {
        _accounts.GetUserInfoAsync(SystemUserIds.GateTerminal, Arg.Any<CancellationToken>()).Returns(_ =>
            new ValueTask<UserInfo?>(_accountExists
                ? UserInfo.Create(new User { Id = SystemUserIds.GateTerminal }, [], [], null)
                : null));
        _provisioning.CreateAccountAsync(SystemUserIds.GateTerminal, Arg.Any<string>(), Arg.Any<Instant>(), Arg.Any<Instant?>())
            .Returns(_ =>
            {
                _accountExists = true;
                return Array.Empty<string>();
            });
        _signIn.HasPasswordAsync(SystemUserIds.GateTerminal).Returns(_ => _password is not null);
        _signIn.SetPasswordAsync(SystemUserIds.GateTerminal, Arg.Any<string>()).Returns(_ =>
        {
            _password = "password-set";
            return IdentityResult.Success;
        });
        _profiles.SaveProfileAsync(Arg.Any<Guid>(), Arg.Any<ProfileSaveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ProfileEditResult(true));
    }

    [HumansTheory]
    [InlineData(AccountScenario.Merged)]
    [InlineData(AccountScenario.Missing)]
    [InlineData(AccountScenario.Cancelled)]
    public async Task PasswordController_ResolvesItsActorBeforeTouchingCredentials(AccountScenario account)
    {
        using var request = new CancellationTokenSource();
        SeedAccount(password: "existing");
        var cookieId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(cookieId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new ValueTask<UserInfo?>(account == AccountScenario.Missing
                ? null : UserInfo.Create(new User { Id = actorId }, [], [], null));
        });
        var http = new DefaultHttpContext
        {
            RequestAborted = request.Token,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, cookieId.ToString())], "test"))
        };
        var controller = new TicketsGateAdminController(users, NewService())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
            Url = Substitute.For<IUrlHelper>()
        };
        if (account == AccountScenario.Cancelled)
        {
            await request.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.SetPassword("password"));
        }
        else
        {
            var result = await controller.SetPassword("password");
            if (account == AccountScenario.Merged)
            {
                Assert.IsType<RedirectToActionResult>(result);
                await _userWrite.Received(1).MarkFakeAsync(SystemUserIds.GateTerminal, actorId, Arg.Any<string>(), Arg.Any<CancellationToken>());
                await _audit.Received(1).LogAsync(TicketsAuditActions.GateTerminalPasswordSet, AuditSubjectTypes.User,
                    SystemUserIds.GateTerminal, "Gate terminal password set", actorId);
            }
            else Assert.IsType<ChallengeResult>(result);
        }
        if (account != AccountScenario.Merged)
        {
            Assert.Empty(_signIn.ReceivedCalls());
            Assert.Empty(_provisioning.ReceivedCalls());
            Assert.Empty(_accounts.ReceivedCalls());
            Assert.Empty(_userWrite.ReceivedCalls());
            Assert.Empty(_audit.ReceivedCalls());
        }
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstProfileSaveFailure_RetryCompletesTheExistingAccount(bool throws)
    {
        var attempts = 0;
        _profiles.SaveProfileAsync(Arg.Any<Guid>(), Arg.Any<ProfileSaveRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (++attempts == 1)
                {
                    if (throws) throw new InvalidOperationException("Profile store unavailable");
                    return new ProfileEditResult(false, ErrorKey: "ProfileSaveFailed");
                }
                return new ProfileEditResult(true);
            });
        var service = NewService();
        var actor = Guid.NewGuid();

        if (throws)
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetPasswordAsync("password", actor));
        else
            Assert.False((await service.SetPasswordAsync("password", actor)).Succeeded);
        Assert.True(_accountExists);
        Assert.Null(_password);
        Assert.Empty(_audit.ReceivedCalls());

        Assert.True((await service.SetPasswordAsync("password", actor)).Succeeded);

        Assert.NotNull(_password);
        await _provisioning.Received(1).CreateAccountAsync(
            SystemUserIds.GateTerminal, Arg.Any<string>(), Arg.Any<Instant>(), Arg.Any<Instant?>());
        Assert.Equal(2, attempts);
    }

    [HumansFact]
    public async Task InitialPasswordSet_MarksTheAccountFake()
    {
        var actor = Guid.NewGuid();

        Assert.True((await NewService().SetPasswordAsync("password", actor)).Succeeded);

        await _userWrite.Received(1).MarkFakeAsync(
            SystemUserIds.GateTerminal, actor, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExistingPasswordRotation_DoesNotRepeatAccountSetup()
    {
        SeedAccount(password: "existing");

        Assert.True((await NewService().SetPasswordAsync("replacement", Guid.NewGuid())).Succeeded);

        Assert.Equal("password-set", _password);
        Assert.Empty(_profiles.ReceivedCalls());
        await _provisioning.DidNotReceiveWithAnyArgs().CreateAccountAsync(default, default!, default);
        // An account provisioned before the marker existed picks it up on rotation.
        await _userWrite.Received(1).MarkFakeAsync(
            SystemUserIds.GateTerminal, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RejectedRotation_LeavesTheExistingPasswordAndDoesNotAuditSuccess()
    {
        SeedAccount(password: "existing");
        _signIn.SetPasswordAsync(SystemUserIds.GateTerminal, Arg.Any<string>())
            .Returns(IdentityResult.Failed(new IdentityError { Code = "PasswordTooShort" }));

        Assert.False((await NewService().SetPasswordAsync("short", Guid.NewGuid())).Succeeded);

        Assert.Equal("existing", _password);
        Assert.Empty(_audit.ReceivedCalls());
        Assert.Empty(_profiles.ReceivedCalls());
    }

    private void SeedAccount(string? password)
    {
        _accountExists = true;
        _password = password;
    }

    private GateTerminalAccountSeeder NewService() => new(
        _signIn, _accounts, _provisioning, _profiles, _userWrite, _audit,
        Clock, NullLogger<GateTerminalAccountSeeder>.Instance);
}
