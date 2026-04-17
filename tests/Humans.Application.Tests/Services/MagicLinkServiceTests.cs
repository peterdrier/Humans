using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

public class MagicLinkServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly UserManager<User> _userManager;
    private readonly IEmailService _emailService;
    private readonly IMemoryCache _memoryCache;
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

        _emailService = Substitute.For<IEmailService>();
        _memoryCache = new MemoryCache(new MemoryCacheOptions());

        var dataProtectionProvider = DataProtectionProvider.Create("TestApp");

        var emailSettings = Options.Create(new EmailSettings
        {
            BaseUrl = "https://test.example.com"
        });

        _service = new MagicLinkService(
            _dbContext,
            _userManager,
            _emailService,
            dataProtectionProvider,
            _memoryCache,
            _clock,
            emailSettings,
            NullLogger<MagicLinkService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _userManager.Dispose();
        _memoryCache.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SendMagicLinkAsync_ExistingUserByUserEmail_SendsLoginLink()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            UserName = "alice@gmail.com",
            Email = "alice@gmail.com",
            DisplayName = "Alice",
            CreatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Users.Add(user);
        _dbContext.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "alice@work.com",
            IsVerified = true,
            IsNotificationTarget = false,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        _userManager.FindByIdAsync(userId.ToString()).Returns(user);
        _userManager.UpdateAsync(Arg.Any<User>()).Returns(IdentityResult.Success);

        // Act — search by a secondary verified email
        await _service.SendMagicLinkAsync("alice@work.com", "/dashboard");

        // Assert — login link sent to the address they typed, not the primary
        await _emailService.Received(1).SendMagicLinkLoginAsync(
            "alice@work.com",
            "Alice",
            Arg.Is<string>(url => url.Contains("/Account/MagicLinkConfirm") && url.Contains(userId.ToString())),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendMagicLinkAsync_ExistingUserByPrimaryEmail_SendsLoginLink()
    {
        // Arrange
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
        _dbContext.Users.Add(user);
        _dbContext.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "alice@gmail.com",
            IsVerified = true,
            IsOAuth = true,
            IsNotificationTarget = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        _userManager.FindByIdAsync(userId.ToString()).Returns(user);
        _userManager.UpdateAsync(Arg.Any<User>()).Returns(IdentityResult.Success);

        // Act
        await _service.SendMagicLinkAsync("alice@gmail.com", null);

        // Assert
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
        // Arrange — no user in DB
        _userManager.FindByEmailAsync(Arg.Any<string>()).Returns((User?)null);

        // Act
        await _service.SendMagicLinkAsync("newperson@example.com", "/welcome");

        // Assert — signup link sent
        await _emailService.Received(1).SendMagicLinkSignupAsync(
            "newperson@example.com",
            Arg.Is<string>(url => url.Contains("/Account/MagicLinkSignup")),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendMagicLinkAsync_RateLimited_DoesNotSendEmail()
    {
        // Arrange — user with recent magic link
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
        _dbContext.Users.Add(user);
        _dbContext.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "alice@gmail.com",
            IsVerified = true,
            IsNotificationTarget = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        _userManager.FindByIdAsync(userId.ToString()).Returns(user);

        // Act
        await _service.SendMagicLinkAsync("alice@gmail.com", null);

        // Assert — no email sent due to rate limit
        await _emailService.DidNotReceive().SendMagicLinkLoginAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendMagicLinkAsync_SignupRateLimited_DoesNotSendEmail()
    {
        // Arrange — no user, but request same email twice quickly
        _userManager.FindByEmailAsync(Arg.Any<string>()).Returns((User?)null);

        await _service.SendMagicLinkAsync("newperson@example.com", null);
        _emailService.ClearReceivedCalls();

        // Act — second request within 60s
        await _service.SendMagicLinkAsync("newperson@example.com", null);

        // Assert — second email suppressed
        await _emailService.DidNotReceive().SendMagicLinkSignupAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendMagicLinkAsync_UnverifiedEmail_DoesNotMatch()
    {
        // Arrange — email exists but unverified
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            UserName = "alice@gmail.com",
            Email = "alice@gmail.com",
            DisplayName = "Alice",
            CreatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Users.Add(user);
        _dbContext.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "alice@work.com",
            IsVerified = false, // Not verified
            IsNotificationTarget = false,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        _userManager.FindByEmailAsync(Arg.Any<string>()).Returns((User?)null);

        // Act — search by unverified email
        await _service.SendMagicLinkAsync("alice@work.com", null);

        // Assert — falls through to signup (unverified email not matched)
        await _emailService.Received(1).SendMagicLinkSignupAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void VerifySignupToken_InvalidToken_ReturnsNull()
    {
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

        var result = await _service.VerifyLoginTokenAsync(userId, "bad-token");

        result.Should().BeNull();
    }

    [Fact]
    public async Task FindUserByVerifiedEmailAsync_FindsByUserEmail()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            UserName = "alice@gmail.com",
            Email = "alice@gmail.com",
            DisplayName = "Alice",
            CreatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Users.Add(user);
        _dbContext.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "alice@work.com",
            IsVerified = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        _userManager.FindByIdAsync(userId.ToString()).Returns(user);

        // Act
        var result = await _service.FindUserByVerifiedEmailAsync("alice@work.com");

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(userId);
    }

    [Fact]
    public async Task FindUserByVerifiedEmailAsync_FallsBackToIdentityEmail()
    {
        // Arrange — no UserEmail rows, but user exists in Identity
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, UserName = "test@test.com", Email = "test@test.com" };
        _userManager.FindByEmailAsync("test@test.com").Returns(user);

        // Act
        var result = await _service.FindUserByVerifiedEmailAsync("test@test.com");

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(userId);
    }
}
