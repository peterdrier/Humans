using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Services.AuditLog;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.AuditLog;

public class AuditViewerServiceTests
{
    private static readonly Instant FixedAt = Instant.FromUtc(2026, 4, 30, 17, 0);

    [HumansFact]
    public async Task GetForUserAsync_ResolvesActorAndSubjectDisplayNames()
    {
        var viewer = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var entry = MakeEntry(
            action: AuditAction.ShiftSignupVoluntold,
            actorId: actor,
            entityType: nameof(ShiftSignup),
            entityId: Guid.NewGuid(),
            relatedEntityId: viewer,
            relatedEntityType: "User",
            description: "shift 'Cantina'");

        var auditLog = Substitute.For<IAuditLogService>();
        auditLog.GetByUserAsync(viewer, 10, Arg.Any<CancellationToken>())
            .Returns(new[] { entry });

        var profileService = Substitute.For<IProfileService>();
        profileService.GetByUserIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Profile>
            {
                [actor] = new Profile { UserId = actor, BurnerName = "Frank" },
                [viewer] = new Profile { UserId = viewer, BurnerName = "Peter" }
            });

        var teamService = Substitute.For<ITeamService>();
        teamService.GetByIdsWithParentsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Team>());

        var teamResourceService = Substitute.For<ITeamResourceService>();
        teamResourceService.GetResourceNamesByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>());

        var service = new AuditViewerService(auditLog, profileService, teamService, teamResourceService);

        var events = await service.GetForUserAsync(viewer, 10);

        events.Should().HaveCount(1);
        var ev = events[0];
        ev.ActorDisplayName.Should().Be("Frank");
        ev.SubjectUserId.Should().Be(viewer);
        ev.SubjectDisplayName.Should().Be("Peter");
    }

    [HumansFact]
    public async Task GetForUserAsync_EmptyResult_ReturnsEmptyList()
    {
        var auditLog = Substitute.For<IAuditLogService>();
        auditLog.GetByUserAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<AuditLogEntry>());

        var profileService = Substitute.For<IProfileService>();
        var teamService = Substitute.For<ITeamService>();
        var teamResourceService = Substitute.For<ITeamResourceService>();
        var service = new AuditViewerService(auditLog, profileService, teamService, teamResourceService);

        var events = await service.GetForUserAsync(Guid.NewGuid(), 10);

        events.Should().BeEmpty();
        // No name lookups should fire on empty input — short-circuit guard.
        await profileService.DidNotReceive().GetByUserIdsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetForUserAsync_RoundTripsToRenderPlainText_WithViewerSubstitution()
    {
        var viewer = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var entry = MakeEntry(
            action: AuditAction.ShiftSignupVoluntold,
            actorId: actor,
            entityType: nameof(ShiftSignup),
            entityId: Guid.NewGuid(),
            relatedEntityId: viewer,
            relatedEntityType: "User",
            description: "shift 'Cantina dinner'");

        var (auditLog, profileService, teamService, teamResourceService) = MakeServices(entry, actor, viewer, "Frank", "Peter");
        var service = new AuditViewerService(auditLog, profileService, teamService, teamResourceService);

        var events = await service.GetForUserAsync(viewer, 10);
        var line = events[0].RenderPlainText(viewerUserId: viewer);

        line.Should().Be("2026-04-30 — Frank voluntold You — shift 'Cantina dinner'");
        line.Should().NotContain(viewer.ToString());
        line.Should().NotContain(actor.ToString());
    }

    [HumansFact]
    public async Task GetForResourceAsync_PassesThroughGoogleSyncFields()
    {
        var resourceId = Guid.NewGuid();
        var entry = MakeEntry(
            action: AuditAction.GoogleResourceAccessGranted,
            actorId: null,
            entityType: "GoogleResource",
            entityId: resourceId,
            description: "Granted reader",
            role: "reader",
            userEmail: "p@x",
            success: true,
            syncSource: GoogleSyncSource.ManualSync,
            resourceId: resourceId);

        var auditLog = Substitute.For<IAuditLogService>();
        auditLog.GetByResourceAsync(resourceId).Returns(new[] { entry });

        var profileService = Substitute.For<IProfileService>();
        profileService.GetByUserIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Profile>());

        var teamService = Substitute.For<ITeamService>();
        teamService.GetByIdsWithParentsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Team>());

        var teamResourceService = Substitute.For<ITeamResourceService>();
        teamResourceService.GetResourceNamesByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>());

        var service = new AuditViewerService(auditLog, profileService, teamService, teamResourceService);

        var events = await service.GetForResourceAsync(resourceId);

        events.Should().HaveCount(1);
        var ev = events[0];
        ev.Role.Should().Be("reader");
        ev.UserEmail.Should().Be("p@x");
        ev.SyncSource.Should().Be(GoogleSyncSource.ManualSync);
        ev.Success.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetPageAsync_ResolvesNamesAndRewrapsIntoEvents()
    {
        var actor = Guid.NewGuid();
        var entry = MakeEntry(
            action: AuditAction.MemberSuspended,
            actorId: actor,
            entityType: "User",
            entityId: Guid.NewGuid(),
            description: "Suspended");

        var auditLog = Substitute.For<IAuditLogService>();
        auditLog.GetFilteredAsync(null, 1, 50, Arg.Any<CancellationToken>())
            .Returns((new[] { entry }, 1, 0));

        var profileService = Substitute.For<IProfileService>();
        profileService.GetByUserIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Profile> { [actor] = new Profile { UserId = actor, BurnerName = "Frank" } });

        var teamService = Substitute.For<ITeamService>();
        teamService.GetByIdsWithParentsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Team>());

        var teamResourceService = Substitute.For<ITeamResourceService>();
        teamResourceService.GetResourceNamesByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>());

        var service = new AuditViewerService(auditLog, profileService, teamService, teamResourceService);

        var result = await service.GetPageAsync(null, 1, 50);

        result.TotalCount.Should().Be(1);
        result.Items.Should().HaveCount(1);
        result.Items[0].ActorDisplayName.Should().Be("Frank");
    }

    private static (IAuditLogService, IProfileService, ITeamService, ITeamResourceService) MakeServices(
        AuditLogEntry entry, Guid actor, Guid viewer, string actorName, string viewerName)
    {
        var auditLog = Substitute.For<IAuditLogService>();
        auditLog.GetByUserAsync(viewer, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { entry });

        var profileService = Substitute.For<IProfileService>();
        profileService.GetByUserIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Profile>
            {
                [actor] = new Profile { UserId = actor, BurnerName = actorName },
                [viewer] = new Profile { UserId = viewer, BurnerName = viewerName }
            });

        var teamService = Substitute.For<ITeamService>();
        teamService.GetByIdsWithParentsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Team>());

        var teamResourceService = Substitute.For<ITeamResourceService>();
        teamResourceService.GetResourceNamesByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>());

        return (auditLog, profileService, teamService, teamResourceService);
    }

    private static AuditLogEntry MakeEntry(
        AuditAction action,
        Guid? actorId,
        string entityType,
        Guid entityId,
        Guid? relatedEntityId = null,
        string? relatedEntityType = null,
        string description = "",
        string? role = null,
        string? userEmail = null,
        bool? success = null,
        GoogleSyncSource? syncSource = null,
        Guid? resourceId = null)
    {
        return new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = FixedAt,
            Action = action,
            ActorUserId = actorId,
            EntityType = entityType,
            EntityId = entityId,
            RelatedEntityId = relatedEntityId,
            RelatedEntityType = relatedEntityType,
            Description = description,
            Role = role,
            UserEmail = userEmail,
            Success = success,
            SyncSource = syncSource,
            ResourceId = resourceId
        };
    }
}
