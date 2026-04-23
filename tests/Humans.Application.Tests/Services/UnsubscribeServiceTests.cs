using AwesomeAssertions;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Unit tests for the Application-layer <see cref="UnsubscribeService"/>
/// (§15 migration, issue #558). Dependencies are mocked; the repository
/// replacement simply returns the seeded user by id so we can verify the
/// service's decision paths (valid / expired / legacy / missing user).
/// </summary>
public class UnsubscribeServiceTests
{
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly ICommunicationPreferenceService _preferenceService = Substitute.For<ICommunicationPreferenceService>();
    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();
    private readonly UnsubscribeService _service;

    public UnsubscribeServiceTests()
    {
        _service = new UnsubscribeService(
            _userRepo,
            _preferenceService,
            _dataProtection,
            NullLogger<UnsubscribeService>.Instance);
    }

    private void SeedUser(Guid userId, string displayName)
    {
        _userRepo.GetByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = userId, UserName = $"{userId}@example.com", DisplayName = displayName });
    }

    [Fact]
    public async Task ValidateTokenAsync_ReturnsValid_ForNewFormatToken()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId, "Alice");

        _preferenceService.ValidateUnsubscribeToken("new-token")
            .Returns((TokenValidationStatus.Valid, userId, MessageCategory.Marketing));

        var result = await _service.ValidateTokenAsync("new-token");

        result.IsValid.Should().BeTrue();
        result.IsExpired.Should().BeFalse();
        result.IsLegacy.Should().BeFalse();
        result.UserId.Should().Be(userId);
        result.DisplayName.Should().Be("Alice");
        result.Category.Should().Be(MessageCategory.Marketing);
    }

    [Fact]
    public async Task ValidateTokenAsync_ReturnsExpired_WhenNewTokenExpired()
    {
        _preferenceService.ValidateUnsubscribeToken("expired-token")
            .Returns((TokenValidationStatus.Expired, Guid.Empty, MessageCategory.Marketing));

        var result = await _service.ValidateTokenAsync("expired-token");

        result.IsValid.Should().BeFalse();
        result.IsExpired.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateTokenAsync_FallsBackToLegacy_WhenNewFormatInvalid()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId, "Legacy User");

        _preferenceService.ValidateUnsubscribeToken(Arg.Any<string>())
            .Returns((TokenValidationStatus.Invalid, Guid.Empty, MessageCategory.Marketing));

        // Generate a legacy token using the same protector configuration the service uses.
        var protector = _dataProtection
            .CreateProtector("CampaignUnsubscribe")
            .ToTimeLimitedDataProtector();
        var legacyToken = protector.Protect(userId.ToString(), TimeSpan.FromDays(90));

        var result = await _service.ValidateTokenAsync(legacyToken);

        result.IsValid.Should().BeTrue();
        result.IsLegacy.Should().BeTrue();
        result.UserId.Should().Be(userId);
        result.DisplayName.Should().Be("Legacy User");
        result.Category.Should().Be(MessageCategory.Marketing);
    }

    [Fact]
    public async Task ValidateTokenAsync_ReturnsInvalid_ForGarbageToken()
    {
        _preferenceService.ValidateUnsubscribeToken(Arg.Any<string>())
            .Returns((TokenValidationStatus.Invalid, Guid.Empty, MessageCategory.Marketing));

        var result = await _service.ValidateTokenAsync("totally-not-a-token");

        result.IsValid.Should().BeFalse();
        result.IsExpired.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateTokenAsync_ReturnsInvalid_WhenUserMissingForValidNewToken()
    {
        var userId = Guid.NewGuid();
        _userRepo.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns((User?)null);

        _preferenceService.ValidateUnsubscribeToken("new-token")
            .Returns((TokenValidationStatus.Valid, userId, MessageCategory.Marketing));

        var result = await _service.ValidateTokenAsync("new-token");

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task ConfirmUnsubscribeAsync_CallsUpdatePreferenceAsync_WhenTokenValid()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId, "Alice");

        _preferenceService.ValidateUnsubscribeToken("new-token")
            .Returns((TokenValidationStatus.Valid, userId, MessageCategory.Marketing));

        var result = await _service.ConfirmUnsubscribeAsync("new-token", "MagicLink");

        result.IsValid.Should().BeTrue();
        await _preferenceService.Received(1).UpdatePreferenceAsync(
            userId, MessageCategory.Marketing, true, "MagicLink");
    }

    [Fact]
    public async Task ConfirmUnsubscribeAsync_DoesNotCallUpdate_WhenTokenInvalid()
    {
        _preferenceService.ValidateUnsubscribeToken(Arg.Any<string>())
            .Returns((TokenValidationStatus.Invalid, Guid.Empty, MessageCategory.Marketing));

        var result = await _service.ConfirmUnsubscribeAsync("garbage", "MagicLink");

        result.IsValid.Should().BeFalse();
        await _preferenceService.DidNotReceive().UpdatePreferenceAsync(
            Arg.Any<Guid>(), Arg.Any<MessageCategory>(), Arg.Any<bool>(), Arg.Any<string>());
    }
}
