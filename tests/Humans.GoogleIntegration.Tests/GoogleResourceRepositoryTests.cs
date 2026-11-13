using Xunit;
using Humans.GoogleIntegration.Contracts;
using AwesomeAssertions;
using Humans.GoogleIntegration.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;

namespace Humans.GoogleIntegration.Tests;

public sealed class GoogleResourceRepositoryTests : IDisposable
{
    private readonly DbContextOptions<GoogleIntegrationDbContext> _options;
    private readonly FakeClock _clock;
    private readonly IGoogleResourceRepository _repository;
    private readonly GoogleIntegrationDbContext _seedContext;

    public GoogleResourceRepositoryTests()
    {
        _options = new DbContextOptionsBuilder<GoogleIntegrationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 22, 10, 0));
        _repository = new GoogleResourceRepository(new SingleContextFactory(_options));
        _seedContext = new GoogleIntegrationDbContext(_options);
    }

    public void Dispose()
    {
        _seedContext.Dispose();
    }

    [HumansFact]
    public async Task GetActiveByTeamIdAsync_ExcludesInactive()
    {
        var teamId = Guid.NewGuid();
        var older = Seed(teamId, "older", GoogleResourceType.DriveFolder, Instant.FromUtc(2026, 4, 20, 0, 0));
        var newer = Seed(teamId, "newer", GoogleResourceType.DriveFolder, Instant.FromUtc(2026, 4, 21, 0, 0));
        Seed(teamId, "dead", GoogleResourceType.DriveFolder, Instant.FromUtc(2026, 4, 19, 0, 0), isActive: false);
        await _seedContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var rows = await _repository.GetActiveByTeamIdAsync(teamId, TestContext.Current.CancellationToken);

        rows.Should().HaveCount(2);
        rows.Select(r => r.Id).Should().BeEquivalentTo([older.Id, newer.Id]);
    }

    [HumansFact]
    public async Task GetActiveByTeamIdsAsync_EmptyCollection_ReturnsEmptyDictionary()
    {
        var dict = await _repository.GetActiveByTeamIdsAsync([], TestContext.Current.CancellationToken);
        dict.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetActiveByTeamIdsAsync_MissingTeamsMapToEmptyList()
    {
        var presentTeam = Guid.NewGuid();
        var absentTeam = Guid.NewGuid();
        Seed(presentTeam, "resource", GoogleResourceType.Group);
        await _seedContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var dict = await _repository.GetActiveByTeamIdsAsync([presentTeam, absentTeam], TestContext.Current.CancellationToken);

        dict.Should().ContainKey(presentTeam).WhoseValue.Should().HaveCount(1);
        dict.Should().ContainKey(absentTeam).WhoseValue.Should().BeEmpty(
            because: "callers depend on missing teams mapping to empty rather than missing so Count-based code paths work without null checks");
    }

    [HumansFact]
    public async Task FindActiveByGoogleIdAsync_MatchesOnType()
    {
        var teamId = Guid.NewGuid();
        var folder = Seed(teamId, "match", GoogleResourceType.DriveFolder, googleId: "abc123");
        Seed(teamId, "file-with-same-id", GoogleResourceType.DriveFile, googleId: "abc123");
        await _seedContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var row = await _repository.FindActiveByGoogleIdAsync(teamId, "abc123", GoogleResourceType.DriveFolder, TestContext.Current.CancellationToken);
        row!.Id.Should().Be(folder.Id);

        var missing = await _repository.FindActiveByGoogleIdAsync(teamId, "abc123", GoogleResourceType.Group, TestContext.Current.CancellationToken);
        missing.Should().BeNull();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupEmailLookup_UsesLiteralAddressesWithinTeamTypeAndState(bool inactive)
    {
        var teamId = Guid.NewGuid();
        const string email = "ops_team@nobodies.team";
        Seed(teamId, "different-address", GoogleResourceType.Group,
            isActive: !inactive, googleId: "opsXteam@nobodies.team");
        Seed(Guid.NewGuid(), "other-team", GoogleResourceType.Group,
            isActive: !inactive, googleId: email);
        Seed(teamId, "other-type", GoogleResourceType.DriveFolder,
            isActive: !inactive, googleId: email);
        Seed(teamId, "other-state", GoogleResourceType.Group,
            isActive: inactive, googleId: email);
        await _seedContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        async Task<GoogleResource?> FindAsync() => inactive
            ? await _repository.FindInactiveGroupByCandidatesAsync(teamId, "12345", email, ct: TestContext.Current.CancellationToken)
            : await _repository.FindActiveGroupByEmailAsync(teamId, email, TestContext.Current.CancellationToken);

        (await FindAsync()).Should().BeNull();

        var exact = Seed(teamId, "exact-address", GoogleResourceType.Group,
            isActive: !inactive, googleId: email.ToUpperInvariant());
        await _seedContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var matched = await FindAsync();
        matched.Should().NotBeNull();
        matched!.Id.Should().Be(exact.Id);
    }

    [HumansFact]
    public async Task FindInactiveGroupByCandidatesAsync_PreservesExactNumericIdRecovery()
    {
        var teamId = Guid.NewGuid();
        Seed(teamId, "numeric-prefix", GoogleResourceType.Group, isActive: false, googleId: "123456");
        var exact = Seed(teamId, "numeric-id", GoogleResourceType.Group, isActive: false, googleId: "12345");
        await _seedContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await _repository.FindInactiveGroupByCandidatesAsync(
            teamId, "12345", "renamed@nobodies.team", ct: TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Id.Should().Be(exact.Id);
    }

    [HumansFact]
    public async Task ReactivateAsync_UpdatesNameUrlAndClearsErrorMessage()
    {
        var teamId = Guid.NewGuid();
        var row = Seed(teamId, "stale", GoogleResourceType.DriveFolder, isActive: false);
        row.ErrorMessage = "last sync failed";
        await _seedContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var updated = await _repository.ReactivateAsync(
            row.Id,
            name: "fresh",
            url: "https://example.com/fresh",
            lastSyncedAt: Instant.FromUtc(2026, 4, 22, 12, 0),
            newGoogleId: null,
            newPermissionLevel: DrivePermissionLevel.Manager, ct: TestContext.Current.CancellationToken);

        updated.Should().NotBeNull();
        updated.IsActive.Should().BeTrue();
        updated.ErrorMessage.Should().BeNull();
        updated.Name.Should().Be("fresh");
        updated.Url.Should().Be("https://example.com/fresh");
        updated.DrivePermissionLevel.Should().Be(DrivePermissionLevel.Manager);
    }

    [HumansFact]
    public async Task FindInactiveGroupByCandidatesAsync_UrlRecoveryIsScopedToTeamTypeAndState()
    {
        var ct = TestContext.Current.CancellationToken;
        var teamId = Guid.NewGuid();
        const string url = "https://groups.google.com/a/nobodies.team/g/restored";
        Seed(Guid.NewGuid(), "other team", GoogleResourceType.Group, isActive: false).Url = url;
        Seed(teamId, "other type", GoogleResourceType.DriveFolder, isActive: false).Url = url;
        Seed(teamId, "already active", GoogleResourceType.Group).Url = url;
        await _seedContext.SaveChangesAsync(ct);

        (await _repository.FindInactiveGroupByCandidatesAsync(
            teamId, "restored@nobodies.team", "restored@nobodies.team", url, ct)).Should().BeNull();

        var target = Seed(teamId, "archived numeric group", GoogleResourceType.Group, isActive: false, googleId: "1234567");
        target.Url = url.ToUpperInvariant();
        await _seedContext.SaveChangesAsync(ct);

        var found = await _repository.FindInactiveGroupByCandidatesAsync(
            teamId, "restored@nobodies.team", "restored@nobodies.team", url, ct);

        found!.Id.Should().Be(target.Id);
        found.GoogleId.Should().Be("1234567");
    }

    [HumansFact]
    public async Task ReactivateAsync_ReplacementCannotDeactivateAnotherTeamOrResourceType()
    {
        var ct = TestContext.Current.CancellationToken;
        var teamId = Guid.NewGuid();
        var foreign = Seed(Guid.NewGuid(), "foreign group", GoogleResourceType.Group);
        var folder = Seed(teamId, "folder", GoogleResourceType.DriveFolder);
        var first = Seed(teamId, "first group", GoogleResourceType.Group, isActive: false);
        var second = Seed(teamId, "second group", GoogleResourceType.Group, isActive: false);
        await _seedContext.SaveChangesAsync(ct);

        await _repository.ReactivateAsync(first.Id, first.Name, first.Url, _clock.GetCurrentInstant(),
            null, null, deactivateResourceId: foreign.Id, ct: ct);
        await _repository.ReactivateAsync(second.Id, second.Name, second.Url, _clock.GetCurrentInstant(),
            null, null, deactivateResourceId: folder.Id, ct: ct);

        (await _repository.GetByIdAsync(foreign.Id, ct))!.IsActive.Should().BeTrue();
        (await _repository.GetByIdAsync(folder.Id, ct))!.IsActive.Should().BeTrue();
        (await _repository.GetByIdAsync(first.Id, ct))!.IsActive.Should().BeTrue();
        (await _repository.GetByIdAsync(second.Id, ct))!.IsActive.Should().BeTrue();
    }

    [HumansFact]
    public async Task ReactivateAsync_AppliesNewGoogleIdWhenProvided()
    {
        // Group reactivation overwrites GoogleId because legacy rows may have
        // stored the email where we now store the numeric id.
        var teamId = Guid.NewGuid();
        var row = Seed(teamId, "old-group", GoogleResourceType.Group, isActive: false, googleId: "old@example.com");
        await _seedContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var updated = await _repository.ReactivateAsync(
            row.Id,
            name: "old@example.com",
            url: null,
            lastSyncedAt: _clock.GetCurrentInstant(),
            newGoogleId: "01234567",
            newPermissionLevel: null, ct: TestContext.Current.CancellationToken);

        updated!.GoogleId.Should().Be("01234567");
    }

    [HumansFact]
    public async Task UnlinkAsync_FlipsIsActive_IsIdempotent()
    {
        var row = Seed(Guid.NewGuid(), "r", GoogleResourceType.DriveFile);
        await _seedContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        await _repository.UnlinkAsync(row.Id, TestContext.Current.CancellationToken);
        (await _repository.GetByIdAsync(row.Id, TestContext.Current.CancellationToken))!.IsActive.Should().BeFalse();

        // Second call must not throw.
        await _repository.UnlinkAsync(row.Id, TestContext.Current.CancellationToken);

        // Unknown id must not throw either.
        await _repository.UnlinkAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
    }

    [HumansFact]
    public async Task UpdatePermissionLevelAsync_ReportsTrueOnlyWhenRowExists()
    {
        var row = Seed(Guid.NewGuid(), "r", GoogleResourceType.DriveFolder);
        await _seedContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var updated = await _repository.UpdatePermissionLevelAsync(row.Id, DrivePermissionLevel.Viewer, TestContext.Current.CancellationToken);
        updated.Should().BeTrue();

        var missing = await _repository.UpdatePermissionLevelAsync(Guid.NewGuid(), DrivePermissionLevel.Viewer, TestContext.Current.CancellationToken);
        missing.Should().BeFalse();
    }

    [HumansFact]
    public async Task DeactivateByTeamAsync_WithType_LeavesOtherTypesUntouched()
    {
        // Guards the same reconciliation-ordering bug as
        // TeamResourceServiceDeactivateTests but at the repository level.
        var teamId = Guid.NewGuid();
        var drive = Seed(teamId, "drive", GoogleResourceType.DriveFolder);
        var group = Seed(teamId, "group", GoogleResourceType.Group);
        await _seedContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var deactivated = await _repository.DeactivateByTeamAsync(teamId, GoogleResourceType.DriveFolder, TestContext.Current.CancellationToken);

        deactivated.Select(r => r.Id).Should().BeEquivalentTo([drive.Id]);

        await using var check = new GoogleIntegrationDbContext(_options);
        (await check.GoogleResources.FindAsync(drive.Id, TestContext.Current.CancellationToken))!.IsActive.Should().BeFalse();
        (await check.GoogleResources.FindAsync(group.Id, TestContext.Current.CancellationToken))!.IsActive.Should().BeTrue();
    }

    [HumansFact]
    public async Task DeactivateByTeamAsync_NoRows_ReturnsEmpty()
    {
        var deactivated = await _repository.DeactivateByTeamAsync(Guid.NewGuid(), resourceType: null, ct: TestContext.Current.CancellationToken);
        deactivated.Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetErrorMessageManyAsync_PersistsBoundedErrorsForAllSelectedResources(bool longError)
    {
        var first = Seed(Guid.NewGuid(), "first", GoogleResourceType.DriveFolder);
        var second = Seed(Guid.NewGuid(), "second", GoogleResourceType.Group);
        var unaffected = Seed(Guid.NewGuid(), "unaffected", GoogleResourceType.Group);
        await _seedContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var error = longError ? new string('e', 1999) + "😀" + new string('e', 2000) : "Google unavailable";

        await _repository.SetErrorMessageManyAsync([first.Id, second.Id], error,
            TestContext.Current.CancellationToken);

        await using var check = new GoogleIntegrationDbContext(_options);
        foreach (var id in new[] { first.Id, second.Id })
        {
            var row = await check.GoogleResources.FindAsync([id], TestContext.Current.CancellationToken);
            row!.ErrorMessage.Should().Be(longError ? new string('e', 1999) : error);
        }
        (await check.GoogleResources.FindAsync([unaffected.Id], TestContext.Current.CancellationToken))!
            .ErrorMessage.Should().BeNull();
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

    /// <summary>Backs every <c>CreateDbContextAsync</c> with the same options, like the production factory.</summary>
    private sealed class SingleContextFactory(DbContextOptions<GoogleIntegrationDbContext> options)
        : IDbContextFactory<GoogleIntegrationDbContext>
    {
        public GoogleIntegrationDbContext CreateDbContext() => new(options);

        public Task<GoogleIntegrationDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(new GoogleIntegrationDbContext(options));
    }
}
