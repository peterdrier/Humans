using Humans.GoogleIntegration.Contracts;
using AwesomeAssertions;
using Xunit;
using Humans.Teams.Contracts;
using Humans.GoogleIntegration.Services;
using Humans.GoogleIntegration.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Humans.GoogleIntegration.Services.Workspace;
using Humans.AuditLog.Contracts;

namespace Humans.GoogleIntegration.Tests;

/// <summary>
/// Coverage for <see cref="ITeamResourceManagement.DeactivateResourcesForTeamAsync"/> — the
/// side of #494 that flips <c>IsActive</c> and writes audit entries. Drives
/// <see cref="TeamResourceService"/> through the section test harness
/// (<see cref="Infrastructure.GoogleIntegrationTestHarness"/>) and a stubbed
/// <see cref="ITeamResourceGoogleClient"/>.
/// </summary>
public sealed class TeamResourceServiceDeactivateTests : Infrastructure.GoogleIntegrationTestHarness
{
    private const string TeamAuditEntityType = "Team";
    private readonly IGoogleDrivePermissionsClient _drivePermissions;
    private readonly TeamResourceService _service;
    private readonly ITeamResourceGoogleClient _googleClient = Substitute.For<ITeamResourceGoogleClient>();

    public TeamResourceServiceDeactivateTests()
        : base(Instant.FromUtc(2026, 4, 15, 12, 0))
    {
        _drivePermissions = Substitute.For<IGoogleDrivePermissionsClient>();

        IGoogleResourceRepository repository = new GoogleResourceRepository(GoogleIntegrationDbFactory);

        _service = new TeamResourceService(
            repository,
            googleClient: _googleClient,
            drivePermissions: _drivePermissions,
            teamService: Substitute.For<ITeamService>(),
            serviceProvider: new ServiceLocatorBuilder().Build(),
            auditLogService: AuditLog,
            resourceOptions: new TeamResourceManagementOptions(),
            clock: Clock,
            logger: NullLogger<TeamResourceService>.Instance);
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task ResourceAdministration_WritesAuditAfterMutation(bool unlink)
    {
        var teamId = Guid.NewGuid();
        var resourceId = SeedResource(teamId, "Resource", GoogleResourceType.DriveFolder);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var actorId = Guid.NewGuid();
        if (unlink)
            await _service.UnlinkResourceAsync(resourceId, actorUserId: actorId);
        else
            await _service.UpdatePermissionLevelAsync(resourceId, DrivePermissionLevel.Viewer, actorUserId: actorId);

        await AuditLog.Received(1).LogAsync(
            unlink ? AuditAction.GoogleResourceUnlinked : AuditAction.GoogleResourceUpdated, nameof(GoogleResource), resourceId,
            Arg.Any<string>(), actorId, teamId, TeamAuditEntityType);
    }

    [HumansTheory]
    [Xunit.InlineData(GoogleResourceType.DriveFolder)]
    [Xunit.InlineData(GoogleResourceType.DriveFile)]
    [Xunit.InlineData(GoogleResourceType.Group)]
    public async Task LinkResource_AuditsCommittedLink(GoogleResourceType type)
    {
        var teamId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        const string googleId = "abcdefghijklmno";
        var url = type == GoogleResourceType.DriveFolder
            ? $"https://drive.google.com/drive/folders/{googleId}"
            : $"https://drive.google.com/file/d/{googleId}/view";
        _googleClient.GetDriveItemAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new DriveLookupResult(new DriveItem(googleId, "Resource", url,
                type == GoogleResourceType.DriveFolder, "Resource"), null));
        _googleClient.LookupGroupAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GroupLookupResult(new ResolvedGroup(googleId, "crew@nobodies.team", "Crew", "https://groups.google.com/crew"), null));

        var result = type == GoogleResourceType.Group
            ? await _service.LinkGroupAsync(teamId, "crew@nobodies.team", actorUserId: actorId)
            : await _service.LinkDriveResourceAsync(teamId, url, actorUserId: actorId);

        Assert.True(result.Success);
        var row = await GoogleIntegrationDb.GoogleResources.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        await AuditLog.Received(1).LogAsync(AuditAction.GoogleResourceLinked, nameof(GoogleResource), row.Id,
            Arg.Any<string>(), actorId, teamId, TeamAuditEntityType);
    }

    [HumansFact]
    public async Task InheritedAccess_EnforcementFailureStillAuditsCommittedPreference()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var resourceId = SeedResource(teamId, "Resource", GoogleResourceType.DriveFolder);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        _drivePermissions.SetInheritedPermissionsDisabledAsync(Arg.Any<string>(), true, Arg.Any<CancellationToken>())
            .Returns(new GoogleClientError(403, "Denied"));

        var result = await _service.SetRestrictInheritedAccessWithResultAsync(resourceId, true, actorUserId: actorId);

        Assert.False(result.Succeeded);
        var row = await GoogleIntegrationDb.GoogleResources.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        Assert.True(row.RestrictInheritedAccess);
        await AuditLog.Received(1).LogAsync(AuditAction.GoogleResourceUpdated, nameof(GoogleResource), resourceId,
            Arg.Is<string>(text => text.Contains("desired", StringComparison.Ordinal)), actorId, teamId, TeamAuditEntityType);
    }

    [HumansFact]
    public async Task MissingOrRefusedResourceAdministration_DoesNotAudit()
    {
        var actorId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        await _service.UnlinkResourceAsync(missingId, actorUserId: actorId);
        await _service.UpdatePermissionLevelAsync(missingId, DrivePermissionLevel.Viewer, actorUserId: actorId);
        await _service.SetRestrictInheritedAccessWithResultAsync(missingId, true, actorUserId: actorId);
        var drive = await _service.LinkDriveResourceAsync(Guid.NewGuid(), "invalid", actorUserId: actorId);
        var group = await _service.LinkGroupAsync(Guid.NewGuid(), string.Empty, actorUserId: actorId);
        Assert.False(drive.Success);
        Assert.False(group.Success);
        await AuditLog.DidNotReceive().LogAsync(Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), actorId, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task DeactivateResourcesForTeamAsync_FlipsIsActiveAndLogsAudit()
    {
        var teamId = Guid.NewGuid();
        var otherTeamId = Guid.NewGuid();

        SeedResource(teamId, "Doomed Drive", GoogleResourceType.DriveFolder);
        SeedResource(teamId, "Doomed Group", GoogleResourceType.Group);
        SeedResource(otherTeamId, "Safe Drive", GoogleResourceType.DriveFolder);
        // Already-inactive row on target team should not generate a duplicate audit.
        SeedResource(teamId, "Already inactive", GoogleResourceType.DriveFolder, isActive: false);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        await _service.DeactivateResourcesForTeamAsync(teamId, ct: Xunit.TestContext.Current.CancellationToken);

        var doomedRows = await GoogleIntegrationDb.GoogleResources
            .AsNoTracking()
            .Where(r => r.TeamId == teamId)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        doomedRows.Should().OnlyContain(r => !r.IsActive);

        var safeRow = await GoogleIntegrationDb.GoogleResources
            .AsNoTracking()
            .SingleAsync(r => r.TeamId == otherTeamId, Xunit.TestContext.Current.CancellationToken);
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
        SeedResource(teamId, "Doomed Drive", GoogleResourceType.DriveFolder);
        SeedResource(teamId, "Doomed Group", GoogleResourceType.Group);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        await _service.DeactivateResourcesForTeamAsync(teamId, GoogleResourceType.DriveFolder, Xunit.TestContext.Current.CancellationToken);

        var rows = await GoogleIntegrationDb.GoogleResources
            .AsNoTracking()
            .Where(r => r.TeamId == teamId)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        rows.Should().HaveCount(2);
        rows.Single(r => r.ResourceType == GoogleResourceType.DriveFolder).IsActive.Should().BeFalse();
        rows.Single(r => r.ResourceType == GoogleResourceType.Group).IsActive.Should().BeTrue();
    }

    [HumansFact]
    public async Task SetRestrictInheritedAccessWithResultAsync_ReturnsSuccessAndUpdatesFolder()
    {
        var teamId = Guid.NewGuid();
        var resourceId = SeedResource(teamId, "Folder", GoogleResourceType.DriveFolder);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        _drivePermissions.SetInheritedPermissionsDisabledAsync(
                Arg.Any<string>(),
                true,
                Arg.Any<CancellationToken>())
            .Returns((GoogleClientError?)null);

        var result = await _service.SetRestrictInheritedAccessWithResultAsync(resourceId, restrict: true, ct: Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();

        var stored = await GoogleIntegrationDb.GoogleResources.AsNoTracking().SingleAsync(r => r.Id == resourceId, Xunit.TestContext.Current.CancellationToken);
        stored.RestrictInheritedAccess.Should().BeTrue();
        await AuditLog.Received(1).LogAsync(AuditAction.GoogleResourceUpdated, nameof(GoogleResource), resourceId,
            Arg.Any<string>(), nameof(TeamResourceService), teamId, TeamAuditEntityType);
    }

    // ==========================================================================
    // GetResourceNamesByIdsAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetResourceNamesByIdsAsync_EmptyInput_ReturnsEmptyDict()
    {
        var result = await _service.GetResourceNamesByIdsAsync([], Xunit.TestContext.Current.CancellationToken);
        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetResourceNamesByIdsAsync_MixedKnownAndUnknownIds_ReturnsOnlyKnown()
    {
        var teamId = Guid.NewGuid();

        var knownId1 = Guid.NewGuid();
        var knownId2 = Guid.NewGuid();
        var unknownId = Guid.NewGuid();

        GoogleIntegrationDb.GoogleResources.Add(new GoogleResource
        {
            Id = knownId1,
            TeamId = teamId,
            Name = "Folder One",
            GoogleId = "google-1",
            ResourceType = GoogleResourceType.DriveFolder,
            IsActive = true,
            ProvisionedAt = Clock.GetCurrentInstant()
        });
        GoogleIntegrationDb.GoogleResources.Add(new GoogleResource
        {
            Id = knownId2,
            TeamId = teamId,
            Name = "Group Two",
            GoogleId = "google-2",
            ResourceType = GoogleResourceType.Group,
            IsActive = true,
            ProvisionedAt = Clock.GetCurrentInstant()
        });
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.GetResourceNamesByIdsAsync([knownId1, knownId2, unknownId], Xunit.TestContext.Current.CancellationToken);

        result.Should().HaveCount(2);
        result[knownId1].Should().Be("Folder One");
        result[knownId2].Should().Be("Group Two");
        result.ContainsKey(unknownId).Should().BeFalse();
    }

    [HumansFact]
    public async Task DeactivateResourcesForTeamAsync_NoActiveResources_IsNoOp()
    {
        var teamId = Guid.NewGuid();
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        await _service.DeactivateResourcesForTeamAsync(teamId, ct: Xunit.TestContext.Current.CancellationToken);

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
        GoogleIntegrationDb.GoogleResources.Add(new GoogleResource
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
