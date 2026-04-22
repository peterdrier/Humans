using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace Humans.Application.Tests.Repositories;

/// <summary>
/// Behavior tests for <see cref="GoogleResourceRepository"/> — the only
/// non-test file that touches <c>DbSet&lt;GoogleResource&gt;</c> after the
/// §15 <c>#540c</c> migration.
///
/// <para>
/// The tests drive the real repository against an EF in-memory database
/// wrapped in a minimal <see cref="IDbContextFactory{T}"/> so each method
/// creates and disposes its own short-lived context (matching the
/// production singleton-plus-factory shape documented in design-rules §15b).
/// Every scenario below exists because an earlier version of the code — or
/// a prospective refactor — got it wrong and cost real debugging time:
/// grouping preserves the contract the service depends on, reactivation
/// clears error state, bulk deactivation is single-transaction, etc.
/// </para>
/// </summary>
public sealed class GoogleResourceRepositoryTests : IDisposable
{
    private readonly DbContextOptions<HumansDbContext> _options;
    private readonly FakeClock _clock;
    private readonly IGoogleResourceRepository _repository;
    private readonly HumansDbContext _seedContext;

    public GoogleResourceRepositoryTests()
    {
        _options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 22, 10, 0));
        _repository = new GoogleResourceRepository(new SingleContextFactory(_options));
        _seedContext = new HumansDbContext(_options);
    }

    public void Dispose()
    {
        _seedContext.Dispose();
    }

    [Fact]
    public async Task GetActiveByTeamIdAsync_OrdersByProvisionedAt_ExcludesInactive()
    {
        var teamId = Guid.NewGuid();
        var older = Seed(teamId, "older", GoogleResourceType.DriveFolder, Instant.FromUtc(2026, 4, 20, 0, 0));
        var newer = Seed(teamId, "newer", GoogleResourceType.DriveFolder, Instant.FromUtc(2026, 4, 21, 0, 0));
        Seed(teamId, "dead", GoogleResourceType.DriveFolder, Instant.FromUtc(2026, 4, 19, 0, 0), isActive: false);
        await _seedContext.SaveChangesAsync();

        var rows = await _repository.GetActiveByTeamIdAsync(teamId);

        rows.Should().HaveCount(2);
        rows.Select(r => r.Id).Should().ContainInOrder(older.Id, newer.Id);
    }

    [Fact]
    public async Task GetActiveByTeamIdsAsync_EmptyCollection_ReturnsEmptyDictionary()
    {
        var dict = await _repository.GetActiveByTeamIdsAsync(Array.Empty<Guid>());
        dict.Should().BeEmpty();
    }

    [Fact]
    public async Task GetActiveByTeamIdsAsync_MissingTeamsMapToEmptyList()
    {
        var presentTeam = Guid.NewGuid();
        var absentTeam = Guid.NewGuid();
        Seed(presentTeam, "resource", GoogleResourceType.Group);
        await _seedContext.SaveChangesAsync();

        var dict = await _repository.GetActiveByTeamIdsAsync([presentTeam, absentTeam]);

        dict.Should().ContainKey(presentTeam).WhoseValue.Should().HaveCount(1);
        dict.Should().ContainKey(absentTeam).WhoseValue.Should().BeEmpty(
            because: "callers depend on missing teams mapping to empty rather than missing so Count-based code paths work without null checks");
    }

    [Fact]
    public async Task FindActiveByGoogleIdAsync_MatchesOnType()
    {
        var teamId = Guid.NewGuid();
        var folder = Seed(teamId, "match", GoogleResourceType.DriveFolder, googleId: "abc123");
        Seed(teamId, "file-with-same-id", GoogleResourceType.DriveFile, googleId: "abc123");
        await _seedContext.SaveChangesAsync();

        var row = await _repository.FindActiveByGoogleIdAsync(teamId, "abc123", GoogleResourceType.DriveFolder);
        row!.Id.Should().Be(folder.Id);

        var missing = await _repository.FindActiveByGoogleIdAsync(teamId, "abc123", GoogleResourceType.Group);
        missing.Should().BeNull();
    }

    [Fact]
    public async Task ReactivateAsync_UpdatesNameUrlAndClearsErrorMessage()
    {
        var teamId = Guid.NewGuid();
        var row = Seed(teamId, "stale", GoogleResourceType.DriveFolder, isActive: false);
        row.ErrorMessage = "last sync failed";
        await _seedContext.SaveChangesAsync();

        var updated = await _repository.ReactivateAsync(
            row.Id,
            name: "fresh",
            url: "https://example.com/fresh",
            lastSyncedAt: Instant.FromUtc(2026, 4, 22, 12, 0),
            newGoogleId: null,
            newPermissionLevel: DrivePermissionLevel.Manager);

        updated.Should().NotBeNull();
        updated!.IsActive.Should().BeTrue();
        updated.ErrorMessage.Should().BeNull();
        updated.Name.Should().Be("fresh");
        updated.Url.Should().Be("https://example.com/fresh");
        updated.DrivePermissionLevel.Should().Be(DrivePermissionLevel.Manager);
    }

    [Fact]
    public async Task ReactivateAsync_AppliesNewGoogleIdWhenProvided()
    {
        // Group reactivation overwrites GoogleId because legacy rows may have
        // stored the email where we now store the numeric id.
        var teamId = Guid.NewGuid();
        var row = Seed(teamId, "old-group", GoogleResourceType.Group, isActive: false, googleId: "old@example.com");
        await _seedContext.SaveChangesAsync();

        var updated = await _repository.ReactivateAsync(
            row.Id,
            name: "old@example.com",
            url: null,
            lastSyncedAt: _clock.GetCurrentInstant(),
            newGoogleId: "01234567",
            newPermissionLevel: null);

        updated!.GoogleId.Should().Be("01234567");
    }

    [Fact]
    public async Task UnlinkAsync_FlipsIsActive_IsIdempotent()
    {
        var row = Seed(Guid.NewGuid(), "r", GoogleResourceType.DriveFile);
        await _seedContext.SaveChangesAsync();

        await _repository.UnlinkAsync(row.Id);
        (await _repository.GetByIdAsync(row.Id))!.IsActive.Should().BeFalse();

        // Second call must not throw.
        await _repository.UnlinkAsync(row.Id);

        // Unknown id must not throw either.
        await _repository.UnlinkAsync(Guid.NewGuid());
    }

    [Fact]
    public async Task UpdatePermissionLevelAsync_ReportsTrueOnlyWhenRowExists()
    {
        var row = Seed(Guid.NewGuid(), "r", GoogleResourceType.DriveFolder);
        await _seedContext.SaveChangesAsync();

        var updated = await _repository.UpdatePermissionLevelAsync(row.Id, DrivePermissionLevel.Viewer);
        updated.Should().BeTrue();

        var missing = await _repository.UpdatePermissionLevelAsync(Guid.NewGuid(), DrivePermissionLevel.Viewer);
        missing.Should().BeFalse();
    }

    [Fact]
    public async Task DeactivateByTeamAsync_WithType_LeavesOtherTypesUntouched()
    {
        // Guards the same reconciliation-ordering bug as
        // TeamResourceServiceDeactivateTests but at the repository level.
        var teamId = Guid.NewGuid();
        var drive = Seed(teamId, "drive", GoogleResourceType.DriveFolder);
        var group = Seed(teamId, "group", GoogleResourceType.Group);
        await _seedContext.SaveChangesAsync();

        var deactivated = await _repository.DeactivateByTeamAsync(teamId, GoogleResourceType.DriveFolder);

        deactivated.Select(r => r.Id).Should().BeEquivalentTo(new[] { drive.Id });

        using var check = new HumansDbContext(_options);
        (await check.GoogleResources.FindAsync(drive.Id))!.IsActive.Should().BeFalse();
        (await check.GoogleResources.FindAsync(group.Id))!.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task DeactivateByTeamAsync_NoRows_ReturnsEmpty()
    {
        var deactivated = await _repository.DeactivateByTeamAsync(Guid.NewGuid(), resourceType: null);
        deactivated.Should().BeEmpty();
    }

    private GoogleResource Seed(
        Guid teamId,
        string name,
        GoogleResourceType type,
        Instant? provisionedAt = null,
        bool isActive = true,
        string? googleId = null)
    {
        var resource = new GoogleResource
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Name = name,
            GoogleId = googleId ?? Guid.NewGuid().ToString(),
            Url = $"https://example.com/{name}",
            ResourceType = type,
            IsActive = isActive,
            ProvisionedAt = provisionedAt ?? _clock.GetCurrentInstant(),
        };
        _seedContext.GoogleResources.Add(resource);
        return resource;
    }

    /// <summary>
    /// Minimal context factory that backs every <c>CreateDbContextAsync</c>
    /// with the same <see cref="DbContextOptions"/>. This mirrors the
    /// production Singleton-plus-factory shape (§15b) without requiring a
    /// real provider.
    /// </summary>
    private sealed class SingleContextFactory : IDbContextFactory<HumansDbContext>
    {
        private readonly DbContextOptions<HumansDbContext> _options;

        public SingleContextFactory(DbContextOptions<HumansDbContext> options) => _options = options;

        public HumansDbContext CreateDbContext() => new(_options);

        public Task<HumansDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(new HumansDbContext(_options));
    }
}
