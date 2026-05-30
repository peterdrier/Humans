using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using MagicLinkService = Humans.Application.Services.Auth.MagicLinkService;

namespace Humans.Application.Tests.Services;

public sealed class MagicLinkServiceTests : ServiceTestHarness
{
    private readonly UserManager<User> _userManager;
    private readonly IUserEmailService _userEmailService;
    private readonly IUserService _userService;
    private readonly IEmailService _emailService;
    private readonly IEmailMessageFactory _emailMessages = Substitute.For<IEmailMessageFactory>();
    private readonly IMagicLinkUrlBuilder _urlBuilder;
    private readonly IMagicLinkRateLimiter _rateLimiter;
    private readonly MagicLinkService _service;

    public MagicLinkServiceTests()
        : base(Instant.FromUtc(2026, 3, 25, 12, 0))
    {
        var store = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            store, null, null, null, null, null, null, null, null);

        _userEmailService = Substitute.For<IUserEmailService>();
        _userService = Substitute.For<IUserService>();

        // Default: no verified UserEmail row exists. Individual tests override
        // by stubbing the service with a UserEmailWithUser.
        _userEmailService
            .FindVerifiedEmailWithUserAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((UserEmailWithUser?)null);

        _emailService = Substitute.For<IEmailService>();
        _urlBuilder = Substitute.For<IMagicLinkUrlBuilder>();
        _urlBuilder.BuildLoginUrl(Arg.Any<Guid>(), Arg.Any<string?>()).Returns(call =>
            $"https://test.example.com/Account/MagicLinkConfirm?userId={call[0]}&token=abc");
        _urlBuilder.BuildSignupUrl(Arg.Any<string>(), Arg.Any<string?>()).Returns(call =>
            $"https://test.example.com/Account/MagicLinkSignup?email={call[0]}&token=abc");

        _rateLimiter = Substitute.For<IMagicLinkRateLimiter>();
        _rateLimiter.TryConsumeLoginTokenAsync(Arg.Any<string>(), Arg.Any<TimeSpan>()).Returns(true);
        _rateLimiter.TryReserveSignupSendAsync(Arg.Any<string>(), Arg.Any<TimeSpan>()).Returns(true);

        _service = new MagicLinkService(
            _userManager,
            _userEmailService,
            _userService,
            _emailService,
            _emailMessages,
            _urlBuilder,
            _rateLimiter,
            Clock,
            NullLogger<MagicLinkService>.Instance);
    }

    public override void Dispose()
    {
        _userManager.Dispose();
        base.Dispose();
    }

    [HumansFact]
    public async Task SendMagicLinkAsync_ExistingUserByUserEmail_SendsLoginLink()
    {
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            UserName = "alice@gmail.com",
            Email = "alice@gmail.com",
            DisplayName = "Alice",
            CreatedAt = Clock.GetCurrentInstant()
        };

        _userEmailService
            .FindVerifiedEmailWithUserAsync("alice@work.com", Arg.Any<CancellationToken>())
            .Returns(new UserEmailWithUser(userId, "alice@work.com", null, null));

        _userManager.FindByIdAsync(userId.ToString()).Returns(user);
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(CreateUserInfo(user));
        _userManager.UpdateAsync(Arg.Any<User>()).Returns(IdentityResult.Success);

        await _service.SendMagicLinkAsync("alice@work.com", "/dashboard");

        _emailMessages.Received(1).MagicLinkLogin(
            "alice@work.com",
            "Alice",
            Arg.Is<string>(url => url.Contains("/Account/MagicLinkConfirm", StringComparison.Ordinal)));
        _urlBuilder.Received(1).BuildLoginUrl(userId, "/dashboard");
    }

    [HumansFact]
    public async Task SendMagicLinkAsync_ExistingUserByVerifiedUserEmail_SendsLoginLink()
    {
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            DisplayName = "Alice",
            CreatedAt = Clock.GetCurrentInstant()
        };

        _userEmailService
            .FindVerifiedEmailWithUserAsync("alice@gmail.com", Arg.Any<CancellationToken>())
            .Returns(new UserEmailWithUser(userId, "alice@gmail.com", null, null));
        _userManager.FindByIdAsync(userId.ToString()).Returns(user);
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(CreateUserInfo(user));
        _userManager.UpdateAsync(Arg.Any<User>()).Returns(IdentityResult.Success);

        await _service.SendMagicLinkAsync("alice@gmail.com", null);

        _emailMessages.Received(1).MagicLinkLogin(
            "alice@gmail.com",
            "Alice",
            Arg.Any<string>());
    }

    [HumansFact]
    public async Task SendMagicLinkAsync_UnknownEmail_SendsSignupLink()
    {
        // Default _userEmailService.FindVerifiedEmailWithUserAsync returns null —
        // no setup needed; the service falls through to signup.
        await _service.SendMagicLinkAsync("newperson@example.com", "/welcome");

        _emailMessages.Received(1).MagicLinkSignup(
            "newperson@example.com",
            Arg.Is<string>(url => url.Contains("/Account/MagicLinkSignup", StringComparison.Ordinal)));
    }

    private static UserInfo CreateUserInfo(User user) =>
        UserInfo.Create(
            user,
            [],
            [],
            [],
            null,
            [],
            [],
            [],
            []);

    [HumansFact]
    public async Task SendMagicLinkAsync_RateLimited_DoesNotSendEmail()
    {
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            UserName = "alice@gmail.com",
            Email = "alice@gmail.com",
            DisplayName = "Alice",
            CreatedAt = Clock.GetCurrentInstant(),
            MagicLinkSentAt = Clock.GetCurrentInstant() - Duration.FromSeconds(30) // 30s ago
        };

        _userEmailService
            .FindVerifiedEmailWithUserAsync("alice@gmail.com", Arg.Any<CancellationToken>())
            .Returns(new UserEmailWithUser(userId, "alice@gmail.com", null, null));
        _userManager.FindByIdAsync(userId.ToString()).Returns(user);

        await _service.SendMagicLinkAsync("alice@gmail.com", null);

        _emailMessages.DidNotReceive().MagicLinkLogin(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task SendMagicLinkAsync_SignupRateLimited_DoesNotSendEmail()
    {
        // First call succeeds
        await _service.SendMagicLinkAsync("newperson@example.com", null);
        _emailMessages.ClearReceivedCalls();

        // Subsequent TryReserveSignupSendAsync returns false
        _rateLimiter.TryReserveSignupSendAsync(Arg.Any<string>(), Arg.Any<TimeSpan>()).Returns(false);

        await _service.SendMagicLinkAsync("newperson@example.com", null);

        _emailMessages.DidNotReceive().MagicLinkSignup(
            Arg.Any<string>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task SendMagicLinkAsync_UnverifiedEmail_DoesNotMatch()
    {
        // The repository-level FindVerifiedEmailWithUserAsync already returns null
        // for unverified rows; the default substitute returns null. Post-PR-2
        // there is no User.Email-column fallback either, so the service routes
        // straight to the signup-link branch.
        await _service.SendMagicLinkAsync("alice@work.com", null);

        _emailMessages.Received(1).MagicLinkSignup(
            Arg.Any<string>(), Arg.Any<string>());
    }

    [HumansFact]
    public void VerifySignupToken_InvalidToken_ReturnsNull()
    {
        _urlBuilder.UnprotectSignupToken(Arg.Any<string>()).Returns((string?)null);
        var result = _service.VerifySignupToken("not-a-valid-token");
        result.Should().BeNull();
    }

    [HumansFact]
    public async Task VerifyLoginTokenAsync_NonexistentUser_ReturnsNull()
    {
        _userManager.FindByIdAsync(Arg.Any<string>()).Returns((User?)null);

        var result = await _service.VerifyLoginTokenAsync(Guid.NewGuid(), "some-token");

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task VerifyLoginTokenAsync_InvalidToken_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, UserName = "test@test.com" };
        _userManager.FindByIdAsync(userId.ToString()).Returns(user);
        _urlBuilder.UnprotectLoginToken("bad-token").Returns((string?)null);

        var result = await _service.VerifyLoginTokenAsync(userId, "bad-token");

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task VerifyLoginTokenAsync_TokenAlreadyConsumed_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, UserName = "test@test.com" };
        _userManager.FindByIdAsync(userId.ToString()).Returns(user);
        _urlBuilder.UnprotectLoginToken("good-token").Returns(userId.ToString());
        _rateLimiter.TryConsumeLoginTokenAsync("good-token", Arg.Any<TimeSpan>()).Returns(false);

        var result = await _service.VerifyLoginTokenAsync(userId, "good-token");

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task VerifyLoginTokenAsync_ValidToken_ReturnsUser()
    {
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, UserName = "test@test.com" };
        _userManager.FindByIdAsync(userId.ToString()).Returns(user);
        _urlBuilder.UnprotectLoginToken("good-token").Returns(userId.ToString());

        var result = await _service.VerifyLoginTokenAsync(userId, "good-token");

        result.Should().NotBeNull();
        result.Id.Should().Be(userId);
    }

    [HumansFact]
    public async Task FindUserByVerifiedEmailAsync_FindsByUserEmail()
    {
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, UserName = "alice@gmail.com", DisplayName = "Alice" };

        _userEmailService
            .FindVerifiedEmailWithUserAsync("alice@work.com", Arg.Any<CancellationToken>())
            .Returns(new UserEmailWithUser(userId, "alice@work.com", null, null));
        _userManager.FindByIdAsync(userId.ToString()).Returns(user);

        var result = await _service.FindUserByVerifiedEmailAsync("alice@work.com");

        result.Should().NotBeNull();
        result.Id.Should().Be(userId);
    }

    [HumansFact]
    public async Task FindUserByVerifiedEmailAsync_NoVerifiedUserEmail_ReturnsNull()
    {
        // Post-PR-2 there is no User.Email column fallback; the only path is
        // through the verified UserEmail row. Default substitute returns null.
        var result = await _service.FindUserByVerifiedEmailAsync("nobody@nowhere.com");

        result.Should().BeNull();
    }
}
