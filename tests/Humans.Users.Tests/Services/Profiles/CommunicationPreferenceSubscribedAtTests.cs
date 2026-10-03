using AwesomeAssertions;
using Humans.Users.Tests.Infrastructure;
using Humans.Base.Configuration;
using Humans.Users.Data.Repositories;
using Humans.Users.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using CommunicationPreferenceService = Humans.Users.Services.CommunicationPreferenceService;
using Humans.Users.Contracts;

namespace Humans.Users.Tests.Services.Profiles;

public sealed class CommunicationPreferenceSubscribedAtTests : ServiceTestHarness
{
    private readonly CommunicationPreferenceService _service;
    private readonly CommunicationPreferenceRepository _repository;

    public CommunicationPreferenceSubscribedAtTests()
        : base(Instant.FromUtc(2026, 5, 1, 12, 0))
    {
        _repository = new CommunicationPreferenceRepository(DbFactory);

        var dataProtectionProvider = DataProtectionProvider.Create("TestApp");
        var emailSettings = Options.Create(new EmailSettings { BaseUrl = "https://test.example.com" });
        var tokenProvider = new UnsubscribeTokenProvider(
            dataProtectionProvider, emailSettings,
            NullLogger<UnsubscribeTokenProvider>.Instance);

        _service = new CommunicationPreferenceService(
            _repository,
            Substitute.For<IUserService>(),
            tokenProvider,
            Clock,
            AuditLog,
            NullLogger<CommunicationPreferenceService>.Instance);
    }

    [HumansFact]
    public async Task UpdatePreferenceAsync_StampsSubscribedAt_OnFirstOptIn()
    {
        var userId = Guid.NewGuid();

        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: false, source: "Profile", cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var prefs = await _repository.GetByUserIdReadOnlyAsync(userId, Xunit.TestContext.Current.CancellationToken);
        var marketing = prefs.Single(p => p.Category == MessageCategory.Marketing);
        marketing.SubscribedAt.Should().NotBeNull();
    }

    [HumansFact]
    public async Task UpdatePreferenceAsync_DoesNotOverwriteSubscribedAt_OnReOptIn()
    {
        var userId = Guid.NewGuid();

        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: false, source: "Profile", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var firstStamp = (await _repository.GetByUserIdReadOnlyAsync(userId, Xunit.TestContext.Current.CancellationToken))
            .Single(p => p.Category == MessageCategory.Marketing).SubscribedAt;

        Clock.Advance(Duration.FromMilliseconds(10));
        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: true, source: "Profile", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: false, source: "Profile", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var laterStamp = (await _repository.GetByUserIdReadOnlyAsync(userId, Xunit.TestContext.Current.CancellationToken))
            .Single(p => p.Category == MessageCategory.Marketing).SubscribedAt;

        laterStamp.Should().Be(firstStamp);
    }

    [HumansFact]
    public async Task UpdatePreferenceAsync_DoesNotStampSubscribedAt_OnNoOpConfirm()
    {
        var userId = Guid.NewGuid();

        // First opt-in: stamps SubscribedAt
        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: false, source: "Profile", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var firstStamp = (await _repository.GetByUserIdReadOnlyAsync(userId, Xunit.TestContext.Current.CancellationToken))
            .Single(p => p.Category == MessageCategory.Marketing).SubscribedAt;

        Clock.Advance(Duration.FromMilliseconds(10));

        // No-op: already opted in, calling again with optedOut:false is idempotent — no state change, no overwrite
        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: false, source: "Profile", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var afterNoOpStamp = (await _repository.GetByUserIdReadOnlyAsync(userId, Xunit.TestContext.Current.CancellationToken))
            .Single(p => p.Category == MessageCategory.Marketing).SubscribedAt;

        afterNoOpStamp.Should().Be(firstStamp);
    }

    [HumansFact]
    public async Task UpdatePreferenceAsync_StampsSubscribedAt_WhenDefaultOptedOutRowTransitionsToOptIn()
    {
        var userId = Guid.NewGuid();

        // Preserve coverage of a historical default row transitioning to an explicit opt-in.
        await _repository.AddAsync(new CommunicationPreference
        {
            Id = Guid.NewGuid(), UserId = userId, Category = MessageCategory.Marketing,
            OptedOut = true, UpdatedAt = Clock.GetCurrentInstant(), UpdateSource = "Default"
        }, Xunit.TestContext.Current.CancellationToken);

        // Now opt in: existing row with OptedOut=true transitions to false
        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: false, source: "Profile", cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var prefs = await _repository.GetByUserIdReadOnlyAsync(userId, Xunit.TestContext.Current.CancellationToken);
        var marketing = prefs.Single(p => p.Category == MessageCategory.Marketing);
        marketing.SubscribedAt.Should().NotBeNull();
    }

    [HumansFact]
    public async Task UpdatePreferenceAsync_FourArg_StampsSubscribedAt_OnFirstOptIn()
    {
        var userId = Guid.NewGuid();

        await _service.UpdatePreferenceAsync(
            userId, MessageCategory.Marketing, optedOut: false, inboxEnabled: true, source: "Profile", cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var prefs = await _repository.GetByUserIdReadOnlyAsync(userId, Xunit.TestContext.Current.CancellationToken);
        var marketing = prefs.Single(p => p.Category == MessageCategory.Marketing);
        marketing.SubscribedAt.Should().NotBeNull();
    }

    [HumansFact]
    public async Task UpdatePreferenceAsync_FourArg_StampsSubscribedAt_OnOptOutToOptInTransition()
    {
        var userId = Guid.NewGuid();

        // Preserve coverage of a historical default row transitioning to an explicit opt-in.
        await _repository.AddAsync(new CommunicationPreference
        {
            Id = Guid.NewGuid(), UserId = userId, Category = MessageCategory.Marketing,
            OptedOut = true, UpdatedAt = Clock.GetCurrentInstant(), UpdateSource = "Default"
        }, Xunit.TestContext.Current.CancellationToken);

        // Transition existing row: opted-out → opted-in via 4-arg overload
        await _service.UpdatePreferenceAsync(
            userId, MessageCategory.Marketing, optedOut: false, inboxEnabled: true, source: "Profile", cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var prefs = await _repository.GetByUserIdReadOnlyAsync(userId, Xunit.TestContext.Current.CancellationToken);
        var marketing = prefs.Single(p => p.Category == MessageCategory.Marketing);
        marketing.SubscribedAt.Should().NotBeNull();
    }
}
