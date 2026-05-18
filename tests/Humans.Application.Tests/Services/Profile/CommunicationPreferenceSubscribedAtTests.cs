using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Profiles;
using Humans.Infrastructure.Services.Profiles;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Interfaces.Users;
using CommunicationPreferenceService = Humans.Application.Services.Profiles.CommunicationPreferenceService;

namespace Humans.Application.Tests.Services.Profiles;

public class CommunicationPreferenceSubscribedAtTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly CommunicationPreferenceService _service;
    private readonly Instant _start = Instant.FromUtc(2026, 5, 1, 12, 0);

    public CommunicationPreferenceSubscribedAtTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(_start);

        var factory = new TestDbContextFactory(options);
        var repository = new CommunicationPreferenceRepository(factory);

        var dataProtectionProvider = DataProtectionProvider.Create("TestApp");
        var emailSettings = Options.Create(new EmailSettings { BaseUrl = "https://test.example.com" });
        var tokenProvider = new UnsubscribeTokenProvider(
            dataProtectionProvider, emailSettings,
            NullLogger<UnsubscribeTokenProvider>.Instance);

        var auditLog = Substitute.For<IAuditLogService>();

        _service = new CommunicationPreferenceService(
            repository,
            Substitute.For<IUserService>(),
            tokenProvider,
            _clock,
            auditLog,
            NullLogger<CommunicationPreferenceService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task UpdatePreferenceAsync_StampsSubscribedAt_OnFirstOptIn()
    {
        var userId = Guid.NewGuid();
        // Seed all defaults so GetPreferencesAsync returns a full set after UpdatePreferenceAsync.
        await _service.GetPreferencesAsync(userId);

        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: false, source: "Profile");

        var prefs = await _service.GetPreferencesAsync(userId);
        var marketing = prefs.Single(p => p.Category == MessageCategory.Marketing);
        marketing.SubscribedAt.Should().NotBeNull();
    }

    [HumansFact]
    public async Task UpdatePreferenceAsync_DoesNotOverwriteSubscribedAt_OnReOptIn()
    {
        var userId = Guid.NewGuid();
        // Seed all defaults so GetPreferencesAsync returns a full set after UpdatePreferenceAsync.
        await _service.GetPreferencesAsync(userId);

        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: false, source: "Profile");
        var firstStamp = (await _service.GetPreferencesAsync(userId))
            .Single(p => p.Category == MessageCategory.Marketing).SubscribedAt;

        _clock.Advance(Duration.FromMilliseconds(10));
        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: true, source: "Profile");
        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: false, source: "Profile");
        var laterStamp = (await _service.GetPreferencesAsync(userId))
            .Single(p => p.Category == MessageCategory.Marketing).SubscribedAt;

        laterStamp.Should().Be(firstStamp);
    }

    [HumansFact]
    public async Task UpdatePreferenceAsync_DoesNotStampSubscribedAt_OnNoOpConfirm()
    {
        var userId = Guid.NewGuid();
        // Seed all defaults so GetPreferencesAsync returns a full set after UpdatePreferenceAsync.
        await _service.GetPreferencesAsync(userId);

        // First opt-in: stamps SubscribedAt
        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: false, source: "Profile");
        var firstStamp = (await _service.GetPreferencesAsync(userId))
            .Single(p => p.Category == MessageCategory.Marketing).SubscribedAt;

        _clock.Advance(Duration.FromMilliseconds(10));

        // No-op: already opted in, calling again with optedOut:false is idempotent — no state change, no overwrite
        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: false, source: "Profile");
        var afterNoOpStamp = (await _service.GetPreferencesAsync(userId))
            .Single(p => p.Category == MessageCategory.Marketing).SubscribedAt;

        afterNoOpStamp.Should().Be(firstStamp);
    }

    [HumansFact]
    public async Task UpdatePreferenceAsync_StampsSubscribedAt_WhenDefaultOptedOutRowTransitionsToOptIn()
    {
        var userId = Guid.NewGuid();

        // Marketing defaults to OptedOut=true — seed defaults first
        await _service.GetPreferencesAsync(userId);

        // Now opt in: existing row with OptedOut=true transitions to false
        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: false, source: "Profile");

        var prefs = await _service.GetPreferencesAsync(userId);
        var marketing = prefs.Single(p => p.Category == MessageCategory.Marketing);
        marketing.SubscribedAt.Should().NotBeNull();
    }

    [HumansFact]
    public async Task UpdatePreferenceAsync_FourArg_StampsSubscribedAt_OnFirstOptIn()
    {
        var userId = Guid.NewGuid();
        // Seed defaults so GetPreferencesAsync returns the full set after Update
        // (matches the 3-arg test pattern).
        await _service.GetPreferencesAsync(userId);

        await _service.UpdatePreferenceAsync(
            userId, MessageCategory.Marketing, optedOut: false, inboxEnabled: true, source: "Profile");

        var prefs = await _service.GetPreferencesAsync(userId);
        var marketing = prefs.Single(p => p.Category == MessageCategory.Marketing);
        marketing.SubscribedAt.Should().NotBeNull();
    }

    [HumansFact]
    public async Task UpdatePreferenceAsync_FourArg_StampsSubscribedAt_OnOptOutToOptInTransition()
    {
        var userId = Guid.NewGuid();

        // Seed defaults (Marketing.OptedOut=true, SubscribedAt=null)
        await _service.GetPreferencesAsync(userId);

        // Transition existing row: opted-out → opted-in via 4-arg overload
        await _service.UpdatePreferenceAsync(
            userId, MessageCategory.Marketing, optedOut: false, inboxEnabled: true, source: "Profile");

        var prefs = await _service.GetPreferencesAsync(userId);
        var marketing = prefs.Single(p => p.Category == MessageCategory.Marketing);
        marketing.SubscribedAt.Should().NotBeNull();
    }
}
