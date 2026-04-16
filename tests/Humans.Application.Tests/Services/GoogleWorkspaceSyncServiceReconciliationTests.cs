using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Tests.Fakes;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

/// <summary>
/// End-to-end coverage for <see cref="GoogleWorkspaceSyncService.SyncResourcesByTypeAsync"/>
/// — the reconciliation path that PR #227 layered multiple fixes onto. See issue #508:
/// every scenario here guards against a specific bug that was found by review instead of by
/// a failing test.
/// </summary>
public sealed class GoogleWorkspaceSyncServiceReconciliationTests : IDisposable
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly HumansDbContext _dbContext;
    private readonly InMemoryDbContextFactory _dbContextFactory;
    private readonly FakeClock _clock;
    private readonly IAuditLogService _auditLogService;
    private readonly StubTeamResourceService _teamResourceService;
    private readonly FakeSyncSettingsService _syncSettings;
    private readonly FakeGoogleGroupMembershipClient _groupClient = new();
    private readonly FakeGoogleDrivePermissionClient _driveClient = new();
    private readonly IServiceProvider _serviceProvider;

    public GoogleWorkspaceSyncServiceReconciliationTests()
    {
        _dbContext = BuildDbContext(_dbName);
        _dbContextFactory = new InMemoryDbContextFactory(_dbName);
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 16, 12, 0));
        _auditLogService = Substitute.For<IAuditLogService>();
        _syncSettings = new FakeSyncSettingsService();

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IAuditLogService)).Returns(_auditLogService);
        serviceProvider.GetService(typeof(ITeamService)).Returns(Substitute.For<ITeamService>());
        _teamResourceService = new StubTeamResourceService(
            _dbContext,
            Options.Create(new TeamResourceManagementSettings()),
            serviceProvider,
            Substitute.For<IRoleAssignmentService>(),
            _clock,
            NullLogger<StubTeamResourceService>.Instance);
        serviceProvider.GetService(typeof(ITeamResourceService)).Returns(_teamResourceService);
        _serviceProvider = serviceProvider;
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    // -------------------------------------------------------------------------
    // Scenario 1: Soft-deleted team — revoke Drive + Group, deactivate per type
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SoftDeletedTeam_DriveFolderPass_RevokesPermissionsAndDeactivatesOnlyDriveRow()
    {
        // Arrange: one soft-deleted team with a Drive folder + Group, both
        // still IsActive = true. Both members have active Google access that
        // should be revoked on the first reconciliation tick.
        _syncSettings.SetMode(SyncServiceType.GoogleDrive, SyncMode.AddAndRemove);
        _syncSettings.SetMode(SyncServiceType.GoogleGroups, SyncMode.AddAndRemove);

        var team = SeedTeam(softDeleted: true);
        var alice = SeedUser("alice@nobodies.team", "Alice");
        var bob = SeedUser("bob@nobodies.team", "Bob");
        SeedLeftMember(team.Id, alice.Id);
        SeedLeftMember(team.Id, bob.Id);

        var driveFolder = SeedResource(team.Id, GoogleResourceType.DriveFolder, "drive-soft-delete");
        var group = SeedResource(team.Id, GoogleResourceType.Group, "group-soft-delete");
        _driveClient.SeedDirectPermission(driveFolder.GoogleId, alice.Email!);
        _driveClient.SeedDirectPermission(driveFolder.GoogleId, bob.Email!);
        _groupClient.SeedMembership(group.GoogleId, alice.Email!);
        _groupClient.SeedMembership(group.GoogleId, bob.Email!);

        await _dbContext.SaveChangesAsync();

        var service = BuildService();

        // Act: run only the DriveFolder pass.
        var result = await service.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute);

        // Assert: diff has no error; Drive permissions are gone from the fake.
        result.Diffs.Should().ContainSingle()
            .Which.ErrorMessage.Should().BeNull();
        _driveClient.GetPermissions(driveFolder.GoogleId).Should().BeEmpty();
        _groupClient.GetMemberships(group.GoogleId).Should().HaveCount(2,
            "the Group pass has not run yet and Drive reconciliation must not touch Group state");

        // The DriveFolder row is deactivated; the Group row is NOT — this is the
        // per-type scoping that guards the Drive-then-Group ordering.
        var rows = await _dbContext.GoogleResources.AsNoTracking()
            .Where(r => r.TeamId == team.Id)
            .ToListAsync();
        rows.Single(r => r.ResourceType == GoogleResourceType.DriveFolder).IsActive.Should().BeFalse();
        rows.Single(r => r.ResourceType == GoogleResourceType.Group).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task SoftDeletedTeam_GroupPass_RevokesMembershipsAndDeactivatesGroupRow()
    {
        _syncSettings.SetMode(SyncServiceType.GoogleDrive, SyncMode.AddAndRemove);
        _syncSettings.SetMode(SyncServiceType.GoogleGroups, SyncMode.AddAndRemove);

        var team = SeedTeam(softDeleted: true);
        var alice = SeedUser("alice@nobodies.team", "Alice");
        SeedLeftMember(team.Id, alice.Id);

        var group = SeedResource(team.Id, GoogleResourceType.Group, "group-only");
        _groupClient.SeedMembership(group.GoogleId, alice.Email!);

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        var result = await service.SyncResourcesByTypeAsync(GoogleResourceType.Group, SyncAction.Execute);

        result.Diffs.Should().ContainSingle()
            .Which.ErrorMessage.Should().BeNull();
        _groupClient.GetMemberships(group.GoogleId).Should().BeEmpty();

        var row = await _dbContext.GoogleResources.AsNoTracking().SingleAsync(r => r.TeamId == team.Id);
        row.IsActive.Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Scenario 2: Shared GoogleId Drive group — error defers ALL rows
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SharedGoogleIdDriveGroup_WhenListFails_NoTeamInGroupIsDeactivated()
    {
        // Arrange: two soft-deleted teams both linked to the same Drive folder.
        // The fake throws on ListPermissionsAsync for that GoogleId, so the
        // diff's ErrorMessage is non-null. Neither row may be deactivated —
        // otherwise the drive row for the "secondary" team is lost forever
        // because SyncDriveResourceGroupAsync only surfaces the primary ResourceId.
        _syncSettings.SetMode(SyncServiceType.GoogleDrive, SyncMode.AddAndRemove);
        _syncSettings.SetMode(SyncServiceType.GoogleGroups, SyncMode.AddAndRemove);

        var teamA = SeedTeam(softDeleted: true, slug: "team-a");
        var teamB = SeedTeam(softDeleted: true, slug: "team-b");
        var alice = SeedUser("alice@nobodies.team", "Alice");
        SeedLeftMember(teamA.Id, alice.Id);
        SeedLeftMember(teamB.Id, alice.Id);

        const string sharedFolderId = "shared-folder-fail";
        SeedResource(teamA.Id, GoogleResourceType.DriveFolder, sharedFolderId);
        SeedResource(teamB.Id, GoogleResourceType.DriveFolder, sharedFolderId);
        _driveClient.FailFile(sharedFolderId);

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        var result = await service.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute);

        // Diff carries the error; both rows stay IsActive = true.
        result.Diffs.Should().ContainSingle()
            .Which.ErrorMessage.Should().NotBeNullOrEmpty();

        var rows = await _dbContext.GoogleResources.AsNoTracking()
            .Where(r => r.GoogleId == sharedFolderId)
            .ToListAsync();
        rows.Should().HaveCount(2);
        rows.Should().AllSatisfy(r => r.IsActive.Should().BeTrue(
            "a failed Drive group must leave every row in the group active so the next tick can retry"));
    }

    // -------------------------------------------------------------------------
    // Scenario 3: Partial error within one team defers deactivation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SoftDeletedTeam_WithOneErroredAndOneCleanDrive_DefersDeactivationForBoth()
    {
        // One soft-deleted team with TWO DriveFolder rows pointing at different
        // GoogleIds. The fake errors on folder-a but succeeds on folder-b.
        // DeactivateResourcesForTeamAsync(team, DriveFolder) is all-or-nothing
        // by design, so the clean row must NOT be swept inactive — otherwise a
        // follow-up call to DeactivateResourcesForTeamAsync would also flip the
        // errored row and its stale access would be lost forever.
        _syncSettings.SetMode(SyncServiceType.GoogleDrive, SyncMode.AddAndRemove);
        _syncSettings.SetMode(SyncServiceType.GoogleGroups, SyncMode.AddAndRemove);

        var team = SeedTeam(softDeleted: true);
        var alice = SeedUser("alice@nobodies.team", "Alice");
        SeedLeftMember(team.Id, alice.Id);

        const string erroredFolder = "folder-a-errors";
        const string cleanFolder = "folder-b-ok";
        SeedResource(team.Id, GoogleResourceType.DriveFolder, erroredFolder);
        SeedResource(team.Id, GoogleResourceType.DriveFolder, cleanFolder);
        _driveClient.SeedDirectPermission(cleanFolder, alice.Email!);
        _driveClient.FailFile(erroredFolder);

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        var result = await service.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute);

        result.Diffs.Should().HaveCount(2);
        // Clean folder had its permission revoked...
        _driveClient.GetPermissions(cleanFolder).Should().BeEmpty();
        // ...but neither row is deactivated because the team has a same-type errored row.
        var rows = await _dbContext.GoogleResources.AsNoTracking()
            .Where(r => r.TeamId == team.Id)
            .ToListAsync();
        rows.Should().HaveCount(2);
        rows.Should().AllSatisfy(r => r.IsActive.Should().BeTrue(
            "a partial error within one team+type defers the entire team from deactivation"));
    }

    // -------------------------------------------------------------------------
    // Scenario 4: DriveFolder pass must NOT deactivate Group row
    // -------------------------------------------------------------------------
    // Merged with scenario 1 — the first test already asserts that the Group row
    // stays IsActive = true after the DriveFolder pass.

    // -------------------------------------------------------------------------
    // Scenario 5: AddOnly mode skips deactivation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SoftDeletedTeam_DriveInAddOnlyMode_DoesNotRevokeOrDeactivate()
    {
        _syncSettings.SetMode(SyncServiceType.GoogleDrive, SyncMode.AddOnly);
        _syncSettings.SetMode(SyncServiceType.GoogleGroups, SyncMode.AddAndRemove);

        var team = SeedTeam(softDeleted: true);
        var alice = SeedUser("alice@nobodies.team", "Alice");
        SeedLeftMember(team.Id, alice.Id);

        var folder = SeedResource(team.Id, GoogleResourceType.DriveFolder, "folder-addonly");
        _driveClient.SeedDirectPermission(folder.GoogleId, alice.Email!);

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        var result = await service.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute);

        result.Diffs.Should().ContainSingle()
            .Which.ErrorMessage.Should().BeNull();

        // The diff correctly reported Alice as Extra, but RemoveUserFromDrive
        // short-circuited because mode != AddAndRemove, and the post-execute
        // deactivation block also skips when mode != AddAndRemove.
        _driveClient.GetPermissions(folder.GoogleId).Should().ContainSingle(p => p.EmailAddress == alice.Email);
        var row = await _dbContext.GoogleResources.AsNoTracking().SingleAsync(r => r.Id == folder.Id);
        row.IsActive.Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Scenario 6: None mode skips deactivation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SoftDeletedTeam_DriveInNoneMode_DoesNotRevokeOrDeactivate()
    {
        _syncSettings.SetMode(SyncServiceType.GoogleDrive, SyncMode.None);
        _syncSettings.SetMode(SyncServiceType.GoogleGroups, SyncMode.AddAndRemove);

        var team = SeedTeam(softDeleted: true);
        var alice = SeedUser("alice@nobodies.team", "Alice");
        SeedLeftMember(team.Id, alice.Id);

        var folder = SeedResource(team.Id, GoogleResourceType.DriveFolder, "folder-none");
        _driveClient.SeedDirectPermission(folder.GoogleId, alice.Email!);

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        var result = await service.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute);

        result.Diffs.Should().ContainSingle().Which.ErrorMessage.Should().BeNull();
        _driveClient.GetPermissions(folder.GoogleId).Should().ContainSingle(p => p.EmailAddress == alice.Email);
        var row = await _dbContext.GoogleResources.AsNoTracking().SingleAsync(r => r.Id == folder.Id);
        row.IsActive.Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private GoogleWorkspaceSyncService BuildService()
    {
        return new GoogleWorkspaceSyncService(
            _dbContext,
            _dbContextFactory,
            Options.Create(new GoogleWorkspaceSettings()),
            _clock,
            _auditLogService,
            _syncSettings,
            _serviceProvider,
            NullLogger<GoogleWorkspaceSyncService>.Instance,
            _groupClient,
            _driveClient);
    }

    private static HumansDbContext BuildDbContext(string name)
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new HumansDbContext(options);
    }

    private Team SeedTeam(bool softDeleted = false, string? slug = null)
    {
        var now = _clock.GetCurrentInstant();
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = slug ?? $"Team-{Guid.NewGuid():N}"[..12],
            Slug = slug ?? Guid.NewGuid().ToString("N")[..12],
            IsActive = !softDeleted,
            CreatedAt = now,
            UpdatedAt = now
        };
        _dbContext.Teams.Add(team);
        return team;
    }

    private User SeedUser(string email, string displayName)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
            DisplayName = displayName,
            GoogleEmail = email,
            GoogleEmailStatus = GoogleEmailStatus.Unknown,
            CreatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Users.Add(user);
        return user;
    }

    private void SeedLeftMember(Guid teamId, Guid userId)
    {
        var now = _clock.GetCurrentInstant();
        _dbContext.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            JoinedAt = now - Duration.FromDays(30),
            LeftAt = now - Duration.FromDays(1)
        });
    }

    private GoogleResource SeedResource(Guid teamId, GoogleResourceType type, string googleId)
    {
        var resource = new GoogleResource
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Name = $"{type}-{googleId}",
            GoogleId = googleId,
            Url = $"https://example.com/{googleId}",
            ResourceType = type,
            IsActive = true,
            DrivePermissionLevel = type == GoogleResourceType.Group
                ? DrivePermissionLevel.None
                : DrivePermissionLevel.Contributor,
            ProvisionedAt = _clock.GetCurrentInstant() - Duration.FromDays(10)
        };
        _dbContext.GoogleResources.Add(resource);
        return resource;
    }

    // Mutable ISyncSettingsService impl so tests can flip modes without a mock setup call.
    private sealed class FakeSyncSettingsService : ISyncSettingsService
    {
        private readonly Dictionary<SyncServiceType, SyncMode> _modes = new();

        public void SetMode(SyncServiceType serviceType, SyncMode mode) => _modes[serviceType] = mode;

        public Task<SyncMode> GetModeAsync(SyncServiceType serviceType, CancellationToken ct = default)
            => Task.FromResult(_modes.TryGetValue(serviceType, out var mode) ? mode : SyncMode.None);

        public Task<List<SyncServiceSettings>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult(_modes
                .Select(kv => new SyncServiceSettings { ServiceType = kv.Key, SyncMode = kv.Value })
                .ToList());

        public Task UpdateModeAsync(SyncServiceType serviceType, SyncMode mode, Guid actorUserId, CancellationToken ct = default)
        {
            _modes[serviceType] = mode;
            return Task.CompletedTask;
        }
    }

    // The sync service's parallel reconciliation path pulls a fresh DbContext per task from
    // the factory. The InMemory provider keys databases by name, so handing out new instances
    // pointing at the same name keeps state coherent with the scoped _dbContext in the test.
    private sealed class InMemoryDbContextFactory : IDbContextFactory<HumansDbContext>
    {
        private readonly string _name;

        public InMemoryDbContextFactory(string name) => _name = name;

        public HumansDbContext CreateDbContext() => BuildDbContext(_name);
    }
}
