using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace Humans.Application.Tests.Services;

file sealed class StubAuditLogService : IAuditLogService
{
    public Task LogAsync(AuditAction action, string entityType, Guid entityId,
        string description, string jobName,
        Guid? relatedEntityId = null, string? relatedEntityType = null) => Task.CompletedTask;

    public Task LogAsync(AuditAction action, string entityType, Guid entityId,
        string description, Guid actorUserId,
        Guid? relatedEntityId = null, string? relatedEntityType = null) => Task.CompletedTask;

    public Task LogGoogleSyncAsync(AuditAction action, Guid resourceId,
        string description, string jobName,
        string userEmail, string role, GoogleSyncSource source, bool success,
        string? errorMessage = null,
        Guid? relatedEntityId = null, string? relatedEntityType = null) => Task.CompletedTask;

    public Task<IReadOnlyList<AuditLogEntry>> GetByResourceAsync(Guid resourceId) =>
        Task.FromResult<IReadOnlyList<AuditLogEntry>>(Array.Empty<AuditLogEntry>());

    public Task<IReadOnlyList<AuditLogEntry>> GetGoogleSyncByUserAsync(Guid userId) =>
        Task.FromResult<IReadOnlyList<AuditLogEntry>>(Array.Empty<AuditLogEntry>());

    public Task<IReadOnlyList<AuditLogEntry>> GetRecentAsync(int count, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditLogEntry>>(Array.Empty<AuditLogEntry>());

    public Task<(IReadOnlyList<AuditLogEntry> Items, int TotalCount, int AnomalyCount)> GetFilteredAsync(
        string? actionFilter, int page, int pageSize, CancellationToken ct = default) =>
        Task.FromResult<(IReadOnlyList<AuditLogEntry>, int, int)>((Array.Empty<AuditLogEntry>(), 0, 0));

    public Task<IReadOnlyList<AuditLogEntry>> GetByUserAsync(Guid userId, int count, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditLogEntry>>(Array.Empty<AuditLogEntry>());

    public Task<IReadOnlyList<AuditLogEntry>> GetFilteredEntriesAsync(
        string? entityType = null,
        Guid? entityId = null,
        Guid? userId = null,
        IReadOnlyList<AuditAction>? actions = null,
        int limit = 20,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditLogEntry>>(Array.Empty<AuditLogEntry>());

    public Task<AuditLogPageResult> GetAuditLogPageAsync(
        string? actionFilter, int page, int pageSize, CancellationToken ct = default) =>
        Task.FromResult(new AuditLogPageResult(
            Array.Empty<AuditLogEntry>(), 0, 0,
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, (string Name, string Slug)>()));

    public Task<Dictionary<Guid, string>> GetUserDisplayNamesAsync(IReadOnlyList<Guid> userIds, CancellationToken ct = default) =>
        Task.FromResult(new Dictionary<Guid, string>());

    public Task<Dictionary<Guid, (string Name, string Slug)>> GetTeamNamesAsync(IReadOnlyList<Guid> teamIds, CancellationToken ct = default) =>
        Task.FromResult(new Dictionary<Guid, (string Name, string Slug)>());
}

public class CommunicationPreferenceServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly CommunicationPreferenceService _service;

    public CommunicationPreferenceServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 1, 12, 0));

        var dataProtectionProvider = DataProtectionProvider.Create("TestApp");

        var emailSettings = Options.Create(new EmailSettings
        {
            BaseUrl = "https://test.example.com"
        });

        _service = new CommunicationPreferenceService(
            _dbContext,
            dataProtectionProvider,
            _clock,
            new StubAuditLogService(),
            emailSettings,
            NullLogger<CommunicationPreferenceService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetPreferencesAsync_CreatesDefaultsForActiveCategories()
    {
        var userId = Guid.NewGuid();

        var prefs = await _service.GetPreferencesAsync(userId);

        // 8 active categories in MessageCategoryExtensions.ActiveCategories
        prefs.Should().HaveCount(8);

        // Deprecated categories must NOT be created
        prefs.Should().NotContain(p => p.Category == MessageCategory.EventOperations);
        prefs.Should().NotContain(p => p.Category == MessageCategory.CommunityUpdates);

        // Rows should be persisted in the database
        var dbCount = await _dbContext.CommunicationPreferences
            .Where(cp => cp.UserId == userId)
            .CountAsync();
        dbCount.Should().Be(8);
    }

    [Fact]
    public async Task GetPreferencesAsync_MarketingDefaultsToOptedOut()
    {
        var userId = Guid.NewGuid();

        var prefs = await _service.GetPreferencesAsync(userId);

        var marketing = prefs.Single(p => p.Category == MessageCategory.Marketing);
        marketing.OptedOut.Should().BeTrue();
    }

    [Fact]
    public async Task UpdatePreferenceAsync_RejectsAlwaysOnCategories()
    {
        var userId = Guid.NewGuid();

        // Attempt to opt out of always-on categories
        await _service.UpdatePreferenceAsync(userId, MessageCategory.System, optedOut: true, source: "Test");
        await _service.UpdatePreferenceAsync(userId, MessageCategory.CampaignCodes, optedOut: true, source: "Test");

        // No rows should have been created (update was silently rejected)
        var systemPref = await _dbContext.CommunicationPreferences
            .FirstOrDefaultAsync(cp => cp.UserId == userId && cp.Category == MessageCategory.System);
        var campaignPref = await _dbContext.CommunicationPreferences
            .FirstOrDefaultAsync(cp => cp.UserId == userId && cp.Category == MessageCategory.CampaignCodes);

        systemPref.Should().BeNull();
        campaignPref.Should().BeNull();
    }

    [Fact]
    public async Task IsOptedOutAsync_ReturnsFalseForAlwaysOnCategories()
    {
        var userId = Guid.NewGuid();

        var systemResult = await _service.IsOptedOutAsync(userId, MessageCategory.System);
        var campaignResult = await _service.IsOptedOutAsync(userId, MessageCategory.CampaignCodes);

        systemResult.Should().BeFalse();
        campaignResult.Should().BeFalse();
    }

    [Fact]
    public async Task AcceptsFacilitatedMessagesAsync_ReturnsTrueByDefault()
    {
        var userId = Guid.NewGuid();

        var result = await _service.AcceptsFacilitatedMessagesAsync(userId);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task AcceptsFacilitatedMessagesAsync_ReturnsFalseWhenOptedOut()
    {
        var userId = Guid.NewGuid();

        await _service.UpdatePreferenceAsync(
            userId, MessageCategory.FacilitatedMessages, optedOut: true, source: "Test");

        var result = await _service.AcceptsFacilitatedMessagesAsync(userId);

        result.Should().BeFalse();
    }
}
