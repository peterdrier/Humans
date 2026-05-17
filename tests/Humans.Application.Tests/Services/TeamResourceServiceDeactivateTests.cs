using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Services.GoogleIntegration;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.GoogleIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Coverage for <see cref="ITeamResourceService.DeactivateResourcesForTeamAsync"/> — the
/// side of #494 that flips <c>IsActive</c> and writes audit entries. After the
/// <c>#540c</c> migration there is a single <see cref="TeamResourceService"/>
/// implementation in the Application layer; this test drives it through an
/// <see cref="IDbContextFactory{HumansDbContext}"/>-backed repository and a
/// stubbed <see cref="ITeamResourceGoogleClient"/>.
/// </summary>
public class TeamResourceServiceDeactivateTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly IAuditLogService _auditLogService;
    private readonly IGoogleDrivePermissionsClient _drivePermissions;
    private readonly TeamResourceService _service;

    public TeamResourceServiceDeactivateTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 15, 12, 0));
        _auditLogService = Substitute.For<IAuditLogService>();
        _drivePermissions = Substitute.For<IGoogleDrivePermissionsClient>();

        var factory = new SingleContextFactory(options);
        IGoogleResourceRepository repository = new GoogleResourceRepository(factory);

        _service = new TeamResourceService(
            repository,
            googleClient: Substitute.For<ITeamResourceGoogleClient>(),
            drivePermissions: _drivePermissions,
            teamService: Substitute.For<ITeamService>(),
            serviceProvider: Substitute.For<IServiceProvider>(),
            auditLogService: _auditLogService,
            resourceOptions: new TeamResourceManagementOptions(),
            clock: _clock,
            logger: NullLogger<TeamResourceService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task DeactivateResourcesForTeamAsync_FlipsIsActiveAndLogsAudit()
    {
        var teamId = Guid.NewGuid();
        var otherTeamId = Guid.NewGuid();
        SeedTeam(teamId, "Doomed");
        SeedTeam(otherTeamId, "Safe");

        SeedResource(teamId, "Doomed Drive", GoogleResourceType.DriveFolder);
        SeedResource(teamId, "Doomed Group", GoogleResourceType.Group);
        SeedResource(otherTeamId, "Safe Drive", GoogleResourceType.DriveFolder);
        // Already-inactive row on target team should not generate a duplicate audit.
        SeedResource(teamId, "Already inactive", GoogleResourceType.DriveFolder, isActive: false);
        await _dbContext.SaveChangesAsync();

        await _service.DeactivateResourcesForTeamAsync(teamId);

        var doomedRows = await _dbContext.GoogleResources
            .AsNoTracking()
            .Where(r => r.TeamId == teamId)
            .ToListAsync();
        doomedRows.Should().OnlyContain(r => !r.IsActive);

        var safeRow = await _dbContext.GoogleResources
            .AsNoTracking()
            .SingleAsync(r => r.TeamId == otherTeamId);
        safeRow.IsActive.Should().BeTrue();

        // Exactly two audit entries (for the two previously-active doomed resources).
        await _auditLogService.Received(2).LogAsync(
            AuditAction.GoogleResourceDeactivated,
            nameof(GoogleResource),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    [HumansFact]
    public async Task DeactivateResourcesForTeamAsync_WithResourceType_OnlyFlipsMatchingType()
    {
        // Guards the reconciliation-ordering bug: the nightly job runs DriveFolder then
        // Group, so when the Drive pass calls this, it must NOT touch the team's Group
        // row — otherwise the Group pass filters r.IsActive, skips it, and leaves
        // Group membership in place.
        var teamId = Guid.NewGuid();
        SeedTeam(teamId, "Doomed");
        SeedResource(teamId, "Doomed Drive", GoogleResourceType.DriveFolder);
        SeedResource(teamId, "Doomed Group", GoogleResourceType.Group);
        await _dbContext.SaveChangesAsync();

        await _service.DeactivateResourcesForTeamAsync(teamId, GoogleResourceType.DriveFolder);

        var rows = await _dbContext.GoogleResources
            .AsNoTracking()
            .Where(r => r.TeamId == teamId)
            .ToListAsync();

        rows.Should().HaveCount(2);
        rows.Single(r => r.ResourceType == GoogleResourceType.DriveFolder).IsActive.Should().BeFalse();
        rows.Single(r => r.ResourceType == GoogleResourceType.Group).IsActive.Should().BeTrue();
    }

    [HumansFact]
    public async Task SetRestrictInheritedAccessWithResultAsync_ReturnsSuccessAndUpdatesFolder()
    {
        var teamId = Guid.NewGuid();
        SeedTeam(teamId, "Access");
        var resourceId = SeedResource(teamId, "Folder", GoogleResourceType.DriveFolder);
        await _dbContext.SaveChangesAsync();
        _drivePermissions.SetInheritedPermissionsDisabledAsync(
                Arg.Any<string>(),
                true,
                Arg.Any<CancellationToken>())
            .Returns((GoogleClientError?)null);

        var result = await _service.SetRestrictInheritedAccessWithResultAsync(resourceId, restrict: true);

        result.Succeeded.Should().BeTrue();

        var stored = await _dbContext.GoogleResources.AsNoTracking().SingleAsync(r => r.Id == resourceId);
        stored.RestrictInheritedAccess.Should().BeTrue();
    }

    // ==========================================================================
    // GetResourceNamesByIdsAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetResourceNamesByIdsAsync_EmptyInput_ReturnsEmptyDict()
    {
        var result = await _service.GetResourceNamesByIdsAsync([]);
        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetResourceNamesByIdsAsync_MixedKnownAndUnknownIds_ReturnsOnlyKnown()
    {
        var teamId = Guid.NewGuid();
        SeedTeam(teamId, "Alpha");

        var knownId1 = Guid.NewGuid();
        var knownId2 = Guid.NewGuid();
        var unknownId = Guid.NewGuid();

        _dbContext.GoogleResources.Add(new GoogleResource
        {
            Id = knownId1,
            TeamId = teamId,
            Name = "Folder One",
            GoogleId = "google-1",
            ResourceType = GoogleResourceType.DriveFolder,
            IsActive = true,
            ProvisionedAt = _clock.GetCurrentInstant()
        });
        _dbContext.GoogleResources.Add(new GoogleResource
        {
            Id = knownId2,
            TeamId = teamId,
            Name = "Group Two",
            GoogleId = "google-2",
            ResourceType = GoogleResourceType.Group,
            IsActive = true,
            ProvisionedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetResourceNamesByIdsAsync([knownId1, knownId2, unknownId]);

        result.Should().HaveCount(2);
        result[knownId1].Should().Be("Folder One");
        result[knownId2].Should().Be("Group Two");
        result.ContainsKey(unknownId).Should().BeFalse();
    }

    [HumansFact]
    public async Task DeactivateResourcesForTeamAsync_NoActiveResources_IsNoOp()
    {
        var teamId = Guid.NewGuid();
        SeedTeam(teamId, "Empty");
        await _dbContext.SaveChangesAsync();

        await _service.DeactivateResourcesForTeamAsync(teamId);

        await _auditLogService.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(),
            Arg.Any<string>(),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    private void SeedTeam(Guid id, string name)
    {
        _dbContext.Teams.Add(new Team
        {
            Id = id,
            Name = name,
            Slug = name.ToLowerInvariant(),
            IsActive = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
    }

    private Guid SeedResource(Guid teamId, string name, GoogleResourceType type, bool isActive = true)
    {
        var id = Guid.NewGuid();
        _dbContext.GoogleResources.Add(new GoogleResource
        {
            Id = id,
            TeamId = teamId,
            Name = name,
            GoogleId = Guid.NewGuid().ToString(),
            Url = $"https://example.com/{name}",
            ResourceType = type,
            IsActive = isActive,
            ProvisionedAt = _clock.GetCurrentInstant()
        });
        return id;
    }

    /// <summary>
    /// Minimal <see cref="IDbContextFactory{HumansDbContext}"/> that reuses a
    /// single in-memory DB across contexts. Each <c>CreateDbContextAsync</c>
    /// returns a fresh <see cref="HumansDbContext"/> over the same
    /// <see cref="DbContextOptions"/>, matching the production behavior where
    /// the repository tears its context down per call.
    /// </summary>
    private sealed class SingleContextFactory(DbContextOptions<HumansDbContext> options)
        : IDbContextFactory<HumansDbContext>
    {
        public HumansDbContext CreateDbContext() => new(options);

        public Task<HumansDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(new HumansDbContext(options));
    }
}
