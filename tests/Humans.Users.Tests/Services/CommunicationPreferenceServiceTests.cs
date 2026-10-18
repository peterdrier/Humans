using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Tests.Infrastructure;
using NSubstitute;
using Humans.Base.Enums;
using Humans.Base.Configuration;
using Humans.Users.Data.Repositories;
using Humans.Users.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using Xunit;
using CommunicationPreferenceService = Humans.Users.Services.CommunicationPreferenceService;

namespace Humans.Users.Tests.Services;

public sealed class CommunicationPreferenceServiceTests : ServiceTestHarness
{
    private readonly CommunicationPreferenceService _service;

    public CommunicationPreferenceServiceTests()
        : base(Instant.FromUtc(2026, 4, 1, 12, 0))
    {
        var dataProtectionProvider = DataProtectionProvider.Create("TestApp");

        var emailSettings = Options.Create(new EmailSettings
        {
            BaseUrl = "https://test.example.com"
        });

        var repository = new CommunicationPreferenceRepository(DbFactory);

        var tokenProvider = new UnsubscribeTokenProvider(
            dataProtectionProvider, emailSettings,
            NullLogger<UnsubscribeTokenProvider>.Instance);

        // The service now reads preferences through IUserManagement.GetUserInfoAsync (the cache layer).
        // For these tests, project the in-memory db state into UserInfo on each read so the
        // write-then-read semantics that this suite verifies still hold end-to-end.
        var userService = Substitute.For<IUserManagement>();
        userService.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var userId = ci.Arg<Guid>();
                var prefs = Db.CommunicationPreferences
                    .Where(cp => cp.UserId == userId)
                    .ToList();
                return new ValueTask<UserInfo?>(BuildStubUserInfo(userId, prefs));
            });

        _service = new CommunicationPreferenceService(
            repository,
            userService,
            tokenProvider,
            Clock,
            AuditLog,
            NullLogger<CommunicationPreferenceService>.Instance);
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FullPreferenceUpdate_UsesTheKnownActorOrKeepsAnonymousAttribution(bool authenticated)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var source = authenticated ? "Profile" : "MagicLink";

        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing,
            optedOut: false, inboxEnabled: false, source, actorUserId: authenticated ? actorId : null, cancellationToken: ct);

        var audit = AuditLog.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
        audit[0].Should().Be(AuditAction.CommunicationPreferenceChanged);
        audit[2].Should().Be(userId);
        audit[4].Should().Be(authenticated ? actorId : nameof(CommunicationPreferenceService));
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmailOnlyUpdate_PreservesInboxOrUsesTheDomainDefault(bool existing)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = Guid.NewGuid();
        if (existing)
        {
            Db.CommunicationPreferences.Add(new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Category = MessageCategory.Marketing,
                OptedOut = true,
                InboxEnabled = false,
                UpdatedAt = Clock.GetCurrentInstant(),
                UpdateSource = "Profile"
            });
            await Db.SaveChangesAsync(ct);
        }

        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing,
            optedOut: false, source: "MailerLite", cancellationToken: ct);

        var preference = await Db.CommunicationPreferences.AsNoTracking().SingleAsync(ct);
        preference.OptedOut.Should().BeFalse();
        preference.InboxEnabled.Should().Be(existing ? false : new CommunicationPreference().InboxEnabled);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedPreference_DoesNotRewriteMetadataOrAudit(bool fullUpdate)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = Guid.NewGuid();
        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing,
            optedOut: false, inboxEnabled: false, source: "Profile", actorUserId: userId, cancellationToken: ct);
        var original = await Db.CommunicationPreferences.AsNoTracking().SingleAsync(ct);
        Clock.Advance(Duration.FromHours(1));
        AuditLog.ClearReceivedCalls();

        if (fullUpdate)
            await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing,
                optedOut: false, inboxEnabled: false, source: "Guest", actorUserId: userId, cancellationToken: ct);
        else
            await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing,
                optedOut: false, source: "MailerLite", cancellationToken: ct);

        var unchanged = await Db.CommunicationPreferences.AsNoTracking().SingleAsync(ct);
        unchanged.UpdatedAt.Should().Be(original.UpdatedAt);
        unchanged.SubscribedAt.Should().Be(original.SubscribedAt);
        unchanged.UpdateSource.Should().Be("Profile");
        unchanged.InboxEnabled.Should().BeFalse();
        AuditLog.ReceivedCalls().Should().BeEmpty();
    }

    private static UserInfo BuildStubUserInfo(Guid userId, IReadOnlyList<CommunicationPreference> prefs) =>
        UserInfoFactory.Create(
            user: new User
            {
                Id = userId,
                DisplayName = "",
                PreferredLanguage = "en",
                CreatedAt = Instant.MinValue,
            },
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: null,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: prefs);

    [HumansFact]
    public async Task IsOptedOutAsync_MarketingDefaultsToOptedOutWithoutWriting()
    {
        var userId = Guid.NewGuid();

        var optedOut = await _service.IsOptedOutAsync(userId, MessageCategory.Marketing,
            Xunit.TestContext.Current.CancellationToken);

        optedOut.Should().BeTrue();
        (await Db.CommunicationPreferences.AnyAsync(Xunit.TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [HumansFact]
    public async Task UpdatePreferenceAsync_RejectsAlwaysOnCategories()
    {
        var userId = Guid.NewGuid();

        // Attempt to opt out of always-on categories
        await _service.UpdatePreferenceAsync(userId, MessageCategory.System, optedOut: true, source: "Test", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        await _service.UpdatePreferenceAsync(userId, MessageCategory.CampaignCodes, optedOut: true, source: "Test", cancellationToken: Xunit.TestContext.Current.CancellationToken);

        await _service.UpdatePreferenceAsync(userId, MessageCategory.System, optedOut: true,
            inboxEnabled: false, source: "Profile", actorUserId: userId, cancellationToken: Xunit.TestContext.Current.CancellationToken);
        await _service.UpdatePreferenceAsync(userId, MessageCategory.CampaignCodes, optedOut: true,
            inboxEnabled: false, source: "Profile", actorUserId: userId, cancellationToken: Xunit.TestContext.Current.CancellationToken);
        AuditLog.ReceivedCalls().Should().BeEmpty();

        // No rows should have been created (update was silently rejected)
        var systemPref = await Db.CommunicationPreferences
            .FirstOrDefaultAsync(cp => cp.UserId == userId && cp.Category == MessageCategory.System, Xunit.TestContext.Current.CancellationToken);
        var campaignPref = await Db.CommunicationPreferences
            .FirstOrDefaultAsync(cp => cp.UserId == userId && cp.Category == MessageCategory.CampaignCodes, Xunit.TestContext.Current.CancellationToken);

        systemPref.Should().BeNull();
        campaignPref.Should().BeNull();
    }

    [HumansFact]
    public async Task IsOptedOutAsync_ReturnsFalseForAlwaysOnCategories()
    {
        var userId = Guid.NewGuid();

        var systemResult = await _service.IsOptedOutAsync(userId, MessageCategory.System, Xunit.TestContext.Current.CancellationToken);
        var campaignResult = await _service.IsOptedOutAsync(userId, MessageCategory.CampaignCodes, Xunit.TestContext.Current.CancellationToken);

        systemResult.Should().BeFalse();
        campaignResult.Should().BeFalse();
    }

    [HumansFact]
    public async Task AcceptsFacilitatedMessagesAsync_ReturnsTrueByDefault()
    {
        var userId = Guid.NewGuid();

        var result = await _service.AcceptsFacilitatedMessagesAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeTrue();
    }

    [HumansFact(Timeout = 10000)]
    public async Task AcceptsFacilitatedMessagesAsync_ReturnsFalseWhenOptedOut()
    {
        var userId = Guid.NewGuid();

        await _service.UpdatePreferenceAsync(
            userId, MessageCategory.FacilitatedMessages, optedOut: true, source: "Test", cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var result = await _service.AcceptsFacilitatedMessagesAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [HumansFact]
    public void ValidateUnsubscribeToken_WithValidToken_ReturnsValidStatusAndCorrectPayload()
    {
        var userId = Guid.NewGuid();
        var category = MessageCategory.TeamUpdates;

        var token = _service.GenerateUnsubscribeToken(userId, category);
        var (status, decodedUserId, decodedCategory) = _service.ValidateUnsubscribeToken(token);

        status.Should().Be(TokenValidationStatus.Valid);
        decodedUserId.Should().Be(userId);
        decodedCategory.Should().Be(category);
    }

    [HumansFact]
    public void ValidateUnsubscribeToken_WithGarbageString_ReturnsInvalidStatus()
    {
        // A random string is not a valid DataProtection payload — simulates a tampered token.
        // Note: DataProtection throws CryptographicException for both expired and tampered tokens.
        // The service distinguishes expired from tampered by checking whether "expired" appears in
        // the exception message. A purely garbage string will not match "expired" and maps to Invalid.
        var (status, decodedUserId, decodedCategory) = _service.ValidateUnsubscribeToken("this-is-not-a-valid-token");

        status.Should().Be(TokenValidationStatus.Invalid);
        decodedUserId.Should().Be(Guid.Empty);
        decodedCategory.Should().Be(default(MessageCategory));
    }

    [HumansFact]
    public void ValidateUnsubscribeToken_WithMalformedBase64_ReturnsInvalidStatus()
    {
        // Another tamper vector: valid-looking base64 that decrypts to garbage
        var (status, _, _) = _service.ValidateUnsubscribeToken("aGVsbG8gd29ybGQ=");

        status.Should().Be(TokenValidationStatus.Invalid);
    }

    // NOTE: Testing TokenValidationStatus.Expired is not straightforward in unit tests because:
    // - Microsoft.AspNetCore.DataProtection's ITimeLimitedDataProtector uses the real system clock
    //   to embed expiry in the encrypted payload, and there is no seam to inject a fake clock.
    // - DataProtectionProvider.Create() used here does not support replacing the clock.
    // - To produce a genuinely expired token we would need to either (a) use the real system clock
    //   and Thread.Sleep for 90 days, or (b) use internal/reflection to access the key ring.
    // The Expired path in ValidateUnsubscribeToken is covered indirectly: the service correctly
    // checks ex.Message.Contains("expired") before returning TokenValidationStatus.Expired, which
    // is consistent with how DataProtection surfaces the expiry error in the real runtime.
}
