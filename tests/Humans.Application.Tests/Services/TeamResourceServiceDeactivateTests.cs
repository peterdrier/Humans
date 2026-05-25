using AwesomeAssertions;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Services.GoogleIntegration;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Repositories.GoogleIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
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
public sealed class TeamResourceServiceDeactivateTests : ServiceTestHarness
{
    private readonly IGoogleDrivePermissionsClient _drivePermissions;
    private readonly TeamResourceService _service;

    public TeamResourceServiceDeactivateTests()
        : base(Instant.FromUtc(2026, 4, 15, 12, 0))
    {
        _drivePermissions = Substitute.For<IGoogleDrivePermissionsClient>();

        IGoogleResourceRepository repository = new GoogleResourceRepository(DbFactory);

        _service = new TeamResourceService(
            repository,
            googleClient: Substitute.For<ITeamResourceGoogleClient>(),
            drivePermissions: _drivePermissions,
            teamService: Substitute.For<ITeamService>(),
            serviceProvider: new ServiceLocatorBuilder().Build(),
            auditLogService: AuditLog,
            resourceOptions: new TeamResourceManagementOptions(),
            clock: Clock,
            logger: NullLogger<TeamResourceService>.Instance);
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
        await Db.SaveChangesAsync();

        await _service.DeactivateResourcesForTeamAsync(teamId);

        var doomedRows = await Db.GoogleResources
            .AsNoTracking()
            .Where(r => r.TeamId == teamId)
            .ToListAsync();
        doomedRows.Should().OnlyContain(r => !r.IsActive);

        var safeRow = await Db.GoogleResources
            .AsNoTracking()
            .SingleAsync(r => r.TeamId == otherTeamId);
        safeRow.IsActive.Should().BeTrue();

        // Exactly two audit entries (for the two previously-active doomed resources).
        await AuditLog.Received(2).LogAsync(
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
        await Db.SaveChangesAsync();

        await _service.DeactivateResourcesForTeamAsync(teamId, GoogleResourceType.DriveFolder);

        var rows = await Db.GoogleResources
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
        await Db.SaveChangesAsync();
        _drivePermissions.SetInheritedPermissionsDisabledAsync(
                Arg.Any<string>(),
                true,
                Arg.Any<CancellationToken>())
            .Returns((GoogleClientError?)null);

        var result = await _service.SetRestrictInheritedAccessWithResultAsync(resourceId, restrict: true);

        result.Succeeded.Should().BeTrue();

        var stored = await Db.GoogleResources.AsNoTracking().SingleAsync(r => r.Id == resourceId);
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

        Db.GoogleResources.Add(new GoogleResource
        {
            Id = knownId1,
            TeamId = teamId,
            Name = "Folder One",
            GoogleId = "google-1",
            ResourceType = GoogleResourceType.DriveFolder,
            IsActive = true,
            ProvisionedAt = Clock.GetCurrentInstant()
        });
        Db.GoogleResources.Add(new GoogleResource
        {
            Id = knownId2,
            TeamId = teamId,
            Name = "Group Two",
            GoogleId = "google-2",
            ResourceType = GoogleResourceType.Group,
            IsActive = true,
            ProvisionedAt = Clock.GetCurrentInstant()
        });
        await Db.SaveChangesAsync();

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
        await Db.SaveChangesAsync();

        await _service.DeactivateResourcesForTeamAsync(teamId);

        await AuditLog.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(),
            Arg.Any<string>(),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    private Guid SeedResource(Guid teamId, string name, GoogleResourceType type, bool isActive = true)
    {
        var id = Guid.NewGuid();
        Db.GoogleResources.Add(new GoogleResource
        {
            Id = id,
            TeamId = teamId,
            Name = name,
            GoogleId = Guid.NewGuid().ToString(),
            Url = $"https://example.com/{name}",
            ResourceType = type,
            IsActive = isActive,
            ProvisionedAt = Clock.GetCurrentInstant()
        });
        return id;
    }
}
