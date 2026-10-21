using AwesomeAssertions;
using Xunit;
using Humans.Users.Contracts;
using Humans.Users.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Users.Tests.Services;

/// <summary>
/// Unit tests for the Application-layer <see cref="UnsubscribeService"/>.
/// Dependencies are mocked; the user service
/// replacement simply returns the seeded user by id so we can verify the
/// service's decision paths (valid / expired / legacy / missing user).
/// </summary>
public class UnsubscribeServiceTests
{
    private readonly IUserManagement _userService = Substitute.For<IUserManagement>();
    private readonly ICommunicationPreferenceService _preferenceService = Substitute.For<ICommunicationPreferenceService>();
    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();
    private readonly UnsubscribeService _service;

    public UnsubscribeServiceTests()
    {
        _service = new UnsubscribeService(
            _userService,
            _preferenceService,
            _dataProtection,
            NullLogger<UnsubscribeService>.Instance);
    }

    private void SeedUser(Guid userId, string displayName)
    {
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(CreateUserInfo(new User { Id = userId, BurnerName = displayName, DisplayName = displayName }));
    }

    // The resolving read hands a merged-away id its survivor.
    private void SeedMergedUser(Guid mergedId, Guid survivorId, string survivorName)
    {
        _userService.GetUserInfoAsync(mergedId, Arg.Any<CancellationToken>())
            .Returns(CreateUserInfo(new User { Id = survivorId, BurnerName = survivorName, DisplayName = survivorName }));
    }

    private static UserInfo CreateUserInfo(User user) =>
        UserInfoFactory.Create(
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
    public async Task ValidateTokenAsync_ReturnsValid_ForNewFormatToken()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId, "Alice");

        _preferenceService.ValidateUnsubscribeToken("new-token")
            .Returns((TokenValidationStatus.Valid, userId, MessageCategory.Marketing));

        var result = await _service.ValidateTokenAsync("new-token", TestContext.Current.CancellationToken);

        result.IsValid.Should().BeTrue();
        result.IsExpired.Should().BeFalse();
        result.IsLegacy.Should().BeFalse();
        result.UserId.Should().Be(userId);
        result.DisplayName.Should().Be("Alice");
        result.Category.Should().Be(MessageCategory.Marketing);
    }

    [HumansFact]
    public async Task ValidateTokenAsync_ReturnsExpired_WhenNewTokenExpired()
    {
        _preferenceService.ValidateUnsubscribeToken("expired-token")
            .Returns((TokenValidationStatus.Expired, Guid.Empty, MessageCategory.Marketing));

        var result = await _service.ValidateTokenAsync("expired-token", TestContext.Current.CancellationToken);

        result.IsValid.Should().BeFalse();
        result.IsExpired.Should().BeTrue();
    }

    [HumansFact]
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

        var result = await _service.ValidateTokenAsync(legacyToken, TestContext.Current.CancellationToken);

        result.IsValid.Should().BeTrue();
        result.IsLegacy.Should().BeTrue();
        result.UserId.Should().Be(userId);
        result.DisplayName.Should().Be("Legacy User");
        result.Category.Should().Be(MessageCategory.Marketing);
    }

    [HumansFact]
    public async Task ValidateTokenAsync_ReturnsInvalid_ForGarbageToken()
    {
        _preferenceService.ValidateUnsubscribeToken(Arg.Any<string>())
            .Returns((TokenValidationStatus.Invalid, Guid.Empty, MessageCategory.Marketing));

        var result = await _service.ValidateTokenAsync("totally-not-a-token", TestContext.Current.CancellationToken);

        result.IsValid.Should().BeFalse();
        result.IsExpired.Should().BeFalse();
    }

    [HumansFact]
    public async Task ValidateTokenAsync_ReturnsInvalid_ForLegacyTokenWithMalformedUserId()
    {
        _preferenceService.ValidateUnsubscribeToken(Arg.Any<string>())
            .Returns((TokenValidationStatus.Invalid, Guid.Empty, MessageCategory.Marketing));

        var protector = _dataProtection
            .CreateProtector("CampaignUnsubscribe")
            .ToTimeLimitedDataProtector();
        var token = protector.Protect("not-a-guid", TimeSpan.FromDays(90));

        var result = await _service.ValidateTokenAsync(token, TestContext.Current.CancellationToken);

        result.IsValid.Should().BeFalse();
        result.IsExpired.Should().BeFalse();
    }

    [HumansFact]
    public async Task ValidateTokenAsync_ReturnsInvalid_WhenUserMissingForValidNewToken()
    {
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns((UserInfo?)null);

        _preferenceService.ValidateUnsubscribeToken("new-token")
            .Returns((TokenValidationStatus.Valid, userId, MessageCategory.Marketing));

        var result = await _service.ValidateTokenAsync("new-token", TestContext.Current.CancellationToken);

        result.IsValid.Should().BeFalse();
    }

    [HumansFact]
    public async Task ConfirmUnsubscribeAsync_CallsUpdatePreferenceAsync_WhenTokenValid()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId, "Alice");

        _preferenceService.ValidateUnsubscribeToken("new-token")
            .Returns((TokenValidationStatus.Valid, userId, MessageCategory.Marketing));

        var result = await _service.ConfirmUnsubscribeAsync("new-token", "MagicLink", TestContext.Current.CancellationToken);

        result.IsValid.Should().BeTrue();
        await _preferenceService.Received(1).UpdatePreferenceAsync(
            userId, MessageCategory.Marketing, true, "MagicLink", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ValidateTokenAsync_ResolvesMergedUser_ToSurvivor()
    {
        var mergedId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        SeedMergedUser(mergedId, survivorId, "Survivor");

        _preferenceService.ValidateUnsubscribeToken("new-token")
            .Returns((TokenValidationStatus.Valid, mergedId, MessageCategory.Marketing));

        var result = await _service.ValidateTokenAsync("new-token", TestContext.Current.CancellationToken);

        result.IsValid.Should().BeTrue();
        result.UserId.Should().Be(survivorId);
        result.DisplayName.Should().Be("Survivor");
    }

    [HumansFact]
    public async Task ConfirmUnsubscribeAsync_WritesPreference_ForSurvivor_WhenTokenIsForMergedUser()
    {
        var mergedId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        SeedMergedUser(mergedId, survivorId, "Survivor");

        _preferenceService.ValidateUnsubscribeToken("new-token")
            .Returns((TokenValidationStatus.Valid, mergedId, MessageCategory.Marketing));

        await _service.ConfirmUnsubscribeAsync("new-token", "MagicLink", TestContext.Current.CancellationToken);

        await _preferenceService.Received(1).UpdatePreferenceAsync(
            survivorId, MessageCategory.Marketing, true, "MagicLink", Arg.Any<CancellationToken>());
        await _preferenceService.DidNotReceive().UpdatePreferenceAsync(
            mergedId, Arg.Any<MessageCategory>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmUnsubscribeAsync_ErasedAccount_DoesNotRecreatePreferences(bool legacy)
    {
        var userId = Guid.NewGuid();
        var erasedUser = CreateUserInfo(new User
        {
            Id = userId,
            DisplayName = "Deleted User",
            Email = $"deleted-{userId:N}@deleted.local"
        });
        erasedUser.IsGdprAnonymized.Should().BeTrue();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(erasedUser);
        var token = "pre-erasure-token";
        if (legacy)
        {
            token = _dataProtection.CreateProtector("CampaignUnsubscribe").ToTimeLimitedDataProtector()
                .Protect(userId.ToString(), TimeSpan.FromDays(90));
            _preferenceService.ValidateUnsubscribeToken(token)
                .Returns((TokenValidationStatus.Invalid, Guid.Empty, MessageCategory.Marketing));
        }
        else
        {
            _preferenceService.ValidateUnsubscribeToken(token)
                .Returns((TokenValidationStatus.Valid, userId, MessageCategory.Marketing));
        }

        var result = await _service.ConfirmUnsubscribeAsync(token, "OneClick", TestContext.Current.CancellationToken);

        result.IsValid.Should().BeFalse();
        result.UserId.Should().BeNull();
        await _preferenceService.DidNotReceive().UpdatePreferenceAsync(
            Arg.Any<Guid>(), Arg.Any<MessageCategory>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ConfirmUnsubscribeAsync_DoesNotCallUpdate_WhenTokenInvalid()
    {
        _preferenceService.ValidateUnsubscribeToken(Arg.Any<string>())
            .Returns((TokenValidationStatus.Invalid, Guid.Empty, MessageCategory.Marketing));

        var result = await _service.ConfirmUnsubscribeAsync("garbage", "MagicLink", TestContext.Current.CancellationToken);

        result.IsValid.Should().BeFalse();
        await _preferenceService.DidNotReceive().UpdatePreferenceAsync(
            Arg.Any<Guid>(), Arg.Any<MessageCategory>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
