using AwesomeAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Xunit;
using Humans.Infrastructure.Repositories.GoogleIntegration;

namespace Humans.Application.Tests.Repositories;

/// <summary>
/// Tests for <see cref="DriveActivityMonitorRepository"/> — the owner of
/// <c>system_settings["DriveActivityMonitor:LastRunAt"]</c>, anomaly audit
/// persistence for the Drive activity monitor, and the
/// Google-OAuth-id → email fallback lookup.
/// </summary>
public sealed class DriveActivityMonitorRepositoryTests : IDisposable
{
    private readonly HumansDbContext _seedContext;
    private readonly TestDbContextFactory _factory;
    private readonly DriveActivityMonitorRepository _repository;

    public DriveActivityMonitorRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _factory = new TestDbContextFactory(options);
        _seedContext = _factory.CreateDbContext();
        _repository = new DriveActivityMonitorRepository(
            _factory,
            NullLogger<DriveActivityMonitorRepository>.Instance);
    }

    public void Dispose()
    {
        _seedContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetLastRunTimestampAsync_ReturnsNull_WhenNoRowExists()
    {
        var result = await _repository.GetLastRunTimestampAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLastRunTimestampAsync_ReturnsNull_WhenValueIsEmpty()
    {
        _seedContext.SystemSettings.Add(new SystemSetting
        {
            Key = DriveActivityMonitorRepository.LastRunSettingKey,
            Value = string.Empty,
        });
        await _seedContext.SaveChangesAsync();

        var result = await _repository.GetLastRunTimestampAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLastRunTimestampAsync_ReturnsNull_WhenValueIsUnparsable()
    {
        _seedContext.SystemSettings.Add(new SystemSetting
        {
            Key = DriveActivityMonitorRepository.LastRunSettingKey,
            Value = "not-an-instant",
        });
        await _seedContext.SaveChangesAsync();

        var result = await _repository.GetLastRunTimestampAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLastRunTimestampAsync_RoundTripsStoredValue()
    {
        var expected = Instant.FromUtc(2026, 4, 22, 10, 15, 30);
        var pattern = NodaTime.Text.InstantPattern.General;
        _seedContext.SystemSettings.Add(new SystemSetting
        {
            Key = DriveActivityMonitorRepository.LastRunSettingKey,
            Value = pattern.Format(expected),
        });
        await _seedContext.SaveChangesAsync();

        var result = await _repository.GetLastRunTimestampAsync();

        result.Should().Be(expected);
    }

    [Fact]
    public async Task PersistAnomaliesAsync_InsertsAnomaliesAndAdvancesMarkerAtomically()
    {
        var marker = Instant.FromUtc(2026, 4, 22, 11, 0);
        var entry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            Action = AuditAction.AnomalousPermissionDetected,
            EntityType = nameof(GoogleResource),
            EntityId = Guid.NewGuid(),
            Description = "test anomaly",
            OccurredAt = marker,
        };

        await _repository.PersistAnomaliesAsync(new[] { entry }, marker);

        await using var verify = _factory.CreateDbContext();
        var savedEntry = await verify.AuditLogEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == entry.Id);
        savedEntry.Should().NotBeNull();
        savedEntry!.Action.Should().Be(AuditAction.AnomalousPermissionDetected);

        var marker2 = await _repository.GetLastRunTimestampAsync();
        marker2.Should().Be(marker);
    }

    [Fact]
    public async Task PersistAnomaliesAsync_WithNullMarker_InsertsAnomaliesButDoesNotAdvanceMarker()
    {
        var existingMarker = Instant.FromUtc(2026, 4, 21, 9, 0);
        _seedContext.SystemSettings.Add(new SystemSetting
        {
            Key = DriveActivityMonitorRepository.LastRunSettingKey,
            Value = NodaTime.Text.InstantPattern.General.Format(existingMarker),
        });
        await _seedContext.SaveChangesAsync();

        var entry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            Action = AuditAction.AnomalousPermissionDetected,
            EntityType = nameof(GoogleResource),
            EntityId = Guid.NewGuid(),
            Description = "partial failure run",
            OccurredAt = existingMarker,
        };

        await _repository.PersistAnomaliesAsync(new[] { entry }, newLastRunAt: null);

        await using var verify = _factory.CreateDbContext();
        var entryCount = await verify.AuditLogEntries.CountAsync();
        entryCount.Should().Be(1);

        var marker = await _repository.GetLastRunTimestampAsync();
        marker.Should().Be(existingMarker, because: "null newLastRunAt means the partial failure path — marker should not advance");
    }

    [Fact]
    public async Task PersistAnomaliesAsync_WithNoAnomaliesAndNullMarker_IsNoOp()
    {
        await _repository.PersistAnomaliesAsync(
            Array.Empty<AuditLogEntry>(), newLastRunAt: null);

        await using var verify = _factory.CreateDbContext();
        (await verify.AuditLogEntries.CountAsync()).Should().Be(0);
        (await verify.SystemSettings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task PersistAnomaliesAsync_UpdatesExistingMarkerRowInPlace()
    {
        var original = Instant.FromUtc(2026, 4, 21, 9, 0);
        _seedContext.SystemSettings.Add(new SystemSetting
        {
            Key = DriveActivityMonitorRepository.LastRunSettingKey,
            Value = NodaTime.Text.InstantPattern.General.Format(original),
        });
        await _seedContext.SaveChangesAsync();

        var next = Instant.FromUtc(2026, 4, 22, 10, 0);
        await _repository.PersistAnomaliesAsync(Array.Empty<AuditLogEntry>(), next);

        await using var verify = _factory.CreateDbContext();
        var rows = await verify.SystemSettings
            .Where(s => s.Key == DriveActivityMonitorRepository.LastRunSettingKey)
            .ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].Value.Should().Be(NodaTime.Text.InstantPattern.General.Format(next));
    }

    [Fact]
    public async Task TryResolveEmailByGoogleUserIdAsync_ReturnsNull_WhenLoginNotFound()
    {
        var result = await _repository.TryResolveEmailByGoogleUserIdAsync("nonexistent");
        result.Should().BeNull();
    }

    [Fact]
    public async Task TryResolveEmailByGoogleUserIdAsync_ReturnsEmail_WhenGoogleLoginExists()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "known@example.com",
            UserName = "known@example.com",
            DisplayName = "Known",
        };
        _seedContext.Users.Add(user);
        _seedContext.Set<IdentityUserLogin<Guid>>().Add(new IdentityUserLogin<Guid>
        {
            LoginProvider = "Google",
            ProviderKey = "google-id-7",
            ProviderDisplayName = "Google",
            UserId = user.Id,
        });
        await _seedContext.SaveChangesAsync();

        var result = await _repository.TryResolveEmailByGoogleUserIdAsync("google-id-7");
        result.Should().Be("known@example.com");
    }

    [Fact]
    public async Task TryResolveEmailByGoogleUserIdAsync_IgnoresNonGoogleLoginsWithSameProviderKey()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "someone@example.com",
            UserName = "someone@example.com",
            DisplayName = "Someone",
        };
        _seedContext.Users.Add(user);
        _seedContext.Set<IdentityUserLogin<Guid>>().Add(new IdentityUserLogin<Guid>
        {
            LoginProvider = "Microsoft",
            ProviderKey = "shared-id",
            ProviderDisplayName = "Microsoft",
            UserId = user.Id,
        });
        await _seedContext.SaveChangesAsync();

        var result = await _repository.TryResolveEmailByGoogleUserIdAsync("shared-id");
        result.Should().BeNull();
    }
}
