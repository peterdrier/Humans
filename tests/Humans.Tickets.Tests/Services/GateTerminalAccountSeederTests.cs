using Humans.AuditLog.Contracts;
using Humans.Consent.Contracts;
using Humans.Base.Constants;
using Humans.Onboarding.Contracts;
using Humans.Tickets.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Tickets.Tests.Services;

public sealed class GateTerminalAccountSeederTests : TicketsTestHarness
{
    private readonly UserManager<User> _users = Substitute.For<UserManager<User>>(
        Substitute.For<IUserStore<User>>(), null, null, null, null, null, null, null, null);
    private readonly UserManager<User> _credentialUsers = Substitute.For<UserManager<User>>(
        Substitute.For<IUserStore<User>>(), null, null, null, null, null, null, null, null);
    private readonly IProfileEditorService _profiles = Substitute.For<IProfileEditorService>();
    private readonly IConsentWrite _consent = Substitute.For<IConsentWrite>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly ServiceProvider _credentialServices;
    private User? _storedUser;

    public GateTerminalAccountSeederTests()
    {
        _credentialServices = new ServiceCollection()
            .AddScoped(_ => _credentialUsers)
            .BuildServiceProvider();
        _users.FindByIdAsync(SystemUserIds.GateTerminal.ToString()).Returns(_ => ReadStoredUser());
        _credentialUsers.FindByIdAsync(SystemUserIds.GateTerminal.ToString()).Returns(_ => ReadStoredUser());
        _users.CreateAsync(Arg.Any<User>()).Returns(call =>
        {
            _storedUser = call.Arg<User>();
            _storedUser = ReadStoredUser();
            return IdentityResult.Success;
        });
        _users.HasPasswordAsync(Arg.Any<User>()).Returns(call => call.Arg<User>().PasswordHash is not null);
        _credentialUsers.GeneratePasswordResetTokenAsync(Arg.Any<User>()).Returns("reset-token");
        _credentialUsers.ResetPasswordAsync(Arg.Any<User>(), "reset-token", Arg.Any<string>()).Returns(call =>
        {
            _storedUser = call.Arg<User>();
            _storedUser.PasswordHash = "password-set";
            return IdentityResult.Success;
        });
        _credentialUsers.SetLockoutEnabledAsync(Arg.Any<User>(), false).Returns(IdentityResult.Success);
        _profiles.SaveProfileAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<ProfileSaveRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _storedUser!.State = UserState.Active;
                _storedUser.FirstName = "Gate";
                _storedUser.LastName = "Terminal";
                return new ProfileEditResult(true);
            });
        _consent.RecordConsentCheckAsync(Arg.Any<Guid>(), Arg.Any<ConsentCheckStatus>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstProfileSaveFailure_RetryCompletesTheExistingAccount(bool throws)
    {
        var attempts = 0;
        _profiles.SaveProfileAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<ProfileSaveRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (++attempts == 1)
                {
                    if (throws) throw new InvalidOperationException("Profile store unavailable");
                    return new ProfileEditResult(false, ErrorKey: "ProfileSaveFailed");
                }
                _storedUser!.State = UserState.Active;
                return new ProfileEditResult(true);
            });
        var service = NewService();
        var actor = Guid.NewGuid();

        if (throws)
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetPasswordAsync("password", actor));
        else
            Assert.False((await service.SetPasswordAsync("password", actor)).Succeeded);
        Assert.NotNull(_storedUser);
        Assert.Null(_storedUser.PasswordHash);
        Assert.Empty(_audit.ReceivedCalls());

        Assert.True((await service.SetPasswordAsync("password", actor)).Succeeded);

        Assert.Equal(UserState.Active, _storedUser.State);
        Assert.NotNull(_storedUser.PasswordHash);
        await _users.Received(1).CreateAsync(Arg.Any<User>());
        Assert.Equal(2, attempts);
    }

    [HumansFact]
    public async Task ConsentAnnotationFailure_LeavesSetupRetryableWithoutSettingPassword()
    {
        _consent.RecordConsentCheckAsync(Arg.Any<Guid>(), Arg.Any<ConsentCheckStatus>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(false, "ConsentCheckFailed"), new OnboardingResult(true));
        var service = NewService();
        var actor = Guid.NewGuid();

        Assert.False((await service.SetPasswordAsync("password", actor)).Succeeded);
        Assert.Null(_storedUser!.PasswordHash);
        Assert.Empty(_audit.ReceivedCalls());

        Assert.True((await service.SetPasswordAsync("password", actor)).Succeeded);
        Assert.NotNull(_storedUser.PasswordHash);
        await _users.Received(1).CreateAsync(Arg.Any<User>());
        await _consent.Received(2).RecordConsentCheckAsync(
            SystemUserIds.GateTerminal, ConsentCheckStatus.Cleared, Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExistingPasswordRotation_DoesNotRepeatAccountSetup()
    {
        _storedUser = new User { Id = SystemUserIds.GateTerminal, State = UserState.Active, PasswordHash = "existing" };

        Assert.True((await NewService().SetPasswordAsync("replacement", Guid.NewGuid())).Succeeded);

        Assert.Equal("password-set", _storedUser.PasswordHash);
        Assert.Empty(_profiles.ReceivedCalls());
        Assert.Empty(_consent.ReceivedCalls());
        await _users.DidNotReceive().CreateAsync(Arg.Any<User>());
    }

    [HumansFact]
    public async Task InitialPasswordSet_PreservesNamesAndActiveStateSavedByProfileSetup()
    {
        Assert.True((await NewService().SetPasswordAsync("password", Guid.NewGuid())).Succeeded);

        Assert.Equal(UserState.Active, _storedUser!.State);
        Assert.Equal("Gate", _storedUser.FirstName);
        Assert.Equal("Terminal", _storedUser.LastName);
        Assert.NotNull(_storedUser.PasswordHash);
        await _credentialUsers.Received(1).ResetPasswordAsync(
            Arg.Is<User>(u => u.State == UserState.Active && u.FirstName == "Gate"), "reset-token", "password");
    }

    [HumansFact]
    public async Task RejectedRotation_LeavesTheExistingPasswordAndDoesNotAuditSuccess()
    {
        _storedUser = new User { Id = SystemUserIds.GateTerminal, State = UserState.Active, PasswordHash = "existing" };
        _credentialUsers.ResetPasswordAsync(Arg.Any<User>(), "reset-token", Arg.Any<string>())
            .Returns(IdentityResult.Failed(new IdentityError { Code = "PasswordTooShort" }));

        Assert.False((await NewService().SetPasswordAsync("short", Guid.NewGuid())).Succeeded);

        Assert.Equal("existing", _storedUser.PasswordHash);
        Assert.Empty(_audit.ReceivedCalls());
        Assert.Empty(_profiles.ReceivedCalls());
    }

    // Identity holds a tracked instance, while profile setup saves through another
    // context. Keep stored rows separate so writing the old instance loses setup.
    private User? ReadStoredUser() => _storedUser is null ? null : new User
    {
        Id = _storedUser.Id,
        State = _storedUser.State,
        BurnerName = _storedUser.BurnerName,
        FirstName = _storedUser.FirstName,
        LastName = _storedUser.LastName,
        PasswordHash = _storedUser.PasswordHash,
    };

    private GateTerminalAccountSeeder NewService() => new(
        _users, _credentialServices.GetRequiredService<IServiceScopeFactory>(),
        _profiles, _consent, Substitute.For<IUserInfoInvalidator>(), _audit,
        Clock, _cache, NullLogger<GateTerminalAccountSeeder>.Instance);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _users.Dispose();
            _credentialServices.Dispose();
            _cache.Dispose();
        }
        base.Dispose(disposing);
    }
}
