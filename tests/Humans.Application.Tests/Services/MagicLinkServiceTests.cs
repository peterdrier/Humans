using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;
using MagicLinkService = Humans.Application.Services.Auth.MagicLinkService;

namespace Humans.Application.Tests.Services;

public class MagicLinkServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly UserManager<User> _userManager;
    private readonly IUserEmailService _userEmailService;
    private readonly IUserRepository _userRepository;
    private readonly IEmailService _emailService;
    private readonly IMagicLinkUrlBuilder _urlBuilder;
    private readonly IMagicLinkRateLimiter _rateLimiter;
    private readonly MagicLinkService _service;

    public MagicLinkServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 25, 12, 0));

        var store = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            store, null, null, null, null, null, null, null, null);

        _userEmailService = Substitute.For<IUserEmailService>();

        // Default: no verified UserEmail row exists. Individual tests override
        // by stubbing the service with a UserEmailWithUser.
        _userEmailService
            .FindVerifiedEmailWithUserAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((UserEmailWithUser?)null);

        _userRepository = Substitute.For<IUserRepository>();
        _userRepository
            .GetByNormalizedEmailAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((User?)null);

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
            _userRepository,
            _emailService,
            _urlBuilder,
            _rateLimiter,
            _clock,
            NullLogger<MagicLinkService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _userManager.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SendMagicLinkAsync_ExistingUserByUserEmail_SendsLoginLink()
    {
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            UserName = "alice@gmail.com",
            Email = "alice@gmail.com",
            DisplayName = "Alice",
            CreatedAt = _clock.GetCurrentInstant()
        };

        _userEmailService
            .FindVerifiedEmailWithUserAsync("alice@work.com", Arg.Any<CancellationToken>())
            .Returns(new UserEmailWithUser(userId, "alice@work.com", null, null));

        _userManager.FindByIdAsync(userId.ToString()).Returns(user);
        _userManager.UpdateAsync(Arg.Any<User>()).Returns(IdentityResult.Success);

        await _service.SendMagicLinkAsync("alice@work.com", "/dashboard");

        await _emailService.Received(1).SendMagicLinkLoginAsync(
            "alice@work.com",
            "Alice",
            Arg.Is<string>(url => url.Contains("/Account/MagicLinkConfirm", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        _urlBuilder.Received(1).BuildLoginUrl(userId, "/dashboard");
    }

    [Fact]
    public async Task SendMagicLinkAsync_ExistingUserByPrimaryEmail_SendsLoginLink()
    {
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            UserName = "alice@gmail.com",
            Email = "alice@gmail.com",
            NormalizedEmail = "ALICE@GMAIL.COM",
            DisplayName = "Alice",
            CreatedAt = _clock.GetCurrentInstant()
        };

        _userManager.FindByEmailAsync("alice@gmail.com").Returns(user);
        _userManager.UpdateAsync(Arg.Any<User>()).Returns(IdentityResult.Success);

        await _service.SendMagicLinkAsync("alice@gmail.com", null);

        await _emailService.Received(1).SendMagicLinkLoginAsync(
            "alice@gmail.com",
            "Alice",
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendMagicLinkAsync_UnknownEmail_SendsSignupLink()
    {
        _userManager.FindByEmailAsync(Arg.Any<string>()).Returns((User?)null);

        await _service.SendMagicLinkAsync("newperson@example.com", "/welcome");

        await _emailService.Received(1).SendMagicLinkSignupAsync(
            "newperson@example.com",
            Arg.Is<string>(url => url.Contains("/Account/MagicLinkSignup", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendMagicLinkAsync_RateLimited_DoesNotSendEmail()
    {
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            UserName = "alice@gmail.com",
            Email = "alice@gmail.com",
            DisplayName = "Alice",
            CreatedAt = _clock.GetCurrentInstant(),
            MagicLinkSentAt = _clock.GetCurrentInstant() - Duration.FromSeconds(30) // 30s ago
        };

        _userEmailService
            .FindVerifiedEmailWithUserAsync("alice@gmail.com", Arg.Any<CancellationToken>())
            .Returns(new UserEmailWithUser(userId, "alice@gmail.com", null, null));
        _userManager.FindByIdAsync(userId.ToString()).Returns(user);

        await _service.SendMagicLinkAsync("alice@gmail.com", null);

        await _emailService.DidNotReceive().SendMagicLinkLoginAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendMagicLinkAsync_SignupRateLimited_DoesNotSendEmail()
    {
        _userManager.FindByEmailAsync(Arg.Any<string>()).Returns((User?)null);

        // First call succeeds
        await _service.SendMagicLinkAsync("newperson@example.com", null);
        _emailService.ClearReceivedCalls();

        // Subsequent TryReserveSignupSendAsync returns false
        _rateLimiter.TryReserveSignupSendAsync(Arg.Any<string>(), Arg.Any<TimeSpan>()).Returns(false);

        await _service.SendMagicLinkAsync("newperson@example.com", null);

        await _emailService.DidNotReceive().SendMagicLinkSignupAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendMagicLinkAsync_UnverifiedEmail_DoesNotMatch()
    {
        // The repository-level FindVerifiedEmailWithUserAsync already returns null
        // for unverified rows; the default substitute returns null.
        _userManager.FindByEmailAsync(Arg.Any<string>()).Returns((User?)null);

        await _service.SendMagicLinkAsync("alice@work.com", null);

        await _emailService.Received(1).SendMagicLinkSignupAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void VerifySignupToken_InvalidToken_ReturnsNull()
    {
        _urlBuilder.UnprotectSignupToken(Arg.Any<string>()).Returns((string?)null);
        var result = _service.VerifySignupToken("not-a-valid-token");
        result.Should().BeNull();
    }

    [Fact]
    public async Task VerifyLoginTokenAsync_NonexistentUser_ReturnsNull()
    {
        _userManager.FindByIdAsync(Arg.Any<string>()).Returns((User?)null);

        var result = await _service.VerifyLoginTokenAsync(Guid.NewGuid(), "some-token");

        result.Should().BeNull();
    }

    [Fact]
    public async Task VerifyLoginTokenAsync_InvalidToken_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, UserName = "test@test.com" };
        _userManager.FindByIdAsync(userId.ToString()).Returns(user);
        _urlBuilder.UnprotectLoginToken("bad-token").Returns((string?)null);

        var result = await _service.VerifyLoginTokenAsync(userId, "bad-token");

        result.Should().BeNull();
    }

    [Fact]
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

    [Fact]
    public async Task VerifyLoginTokenAsync_ValidToken_ReturnsUser()
    {
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, UserName = "test@test.com" };
        _userManager.FindByIdAsync(userId.ToString()).Returns(user);
        _urlBuilder.UnprotectLoginToken("good-token").Returns(userId.ToString());

        var result = await _service.VerifyLoginTokenAsync(userId, "good-token");

        result.Should().NotBeNull();
        result!.Id.Should().Be(userId);
    }

    [Fact]
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
        result!.Id.Should().Be(userId);
    }

    [Fact]
    public async Task FindUserByVerifiedEmailAsync_FallsBackToIdentityEmail()
    {
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, UserName = "test@test.com", Email = "test@test.com" };
        _userManager.FindByEmailAsync("test@test.com").Returns(user);

        var result = await _service.FindUserByVerifiedEmailAsync("test@test.com");

        result.Should().NotBeNull();
        result!.Id.Should().Be(userId);
    }
}
