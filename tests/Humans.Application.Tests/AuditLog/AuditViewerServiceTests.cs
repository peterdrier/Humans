using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
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
        auditLog.GetUserDisplayNamesAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>
            {
                [actor] = "Frank",
                [viewer] = "Peter"
            });
        auditLog.GetTeamNamesAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, (string Name, string Slug)>());

        var service = new AuditViewerService(auditLog);

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
        var service = new AuditViewerService(auditLog);

        var events = await service.GetForUserAsync(Guid.NewGuid(), 10);

        events.Should().BeEmpty();
        // No name lookups should fire on empty input — short-circuit guard.
        await auditLog.DidNotReceive().GetUserDisplayNamesAsync(
            Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>());
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

        var auditLog = MakeAuditLog(entry, actor, viewer, "Frank", "Peter");
        var service = new AuditViewerService(auditLog);

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
        auditLog.GetUserDisplayNamesAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>());
        auditLog.GetTeamNamesAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, (string Name, string Slug)>());

        var service = new AuditViewerService(auditLog);

        var events = await service.GetForResourceAsync(resourceId);

        events.Should().HaveCount(1);
        var ev = events[0];
        ev.Role.Should().Be("reader");
        ev.UserEmail.Should().Be("p@x");
        ev.SyncSource.Should().Be(GoogleSyncSource.ManualSync);
        ev.Success.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetPageAsync_ReusesAuditLogPageQueryAndRewrapsIntoEvents()
    {
        var actor = Guid.NewGuid();
        var entry = MakeEntry(
            action: AuditAction.MemberSuspended,
            actorId: actor,
            entityType: "User",
            entityId: Guid.NewGuid(),
            description: "Suspended");
        var page = new AuditLogPageResult(
            Items: new[] { entry },
            TotalCount: 1,
            AnomalyCount: 0,
            UserDisplayNames: new Dictionary<Guid, string> { [actor] = "Frank" },
            TeamNames: new Dictionary<Guid, (string Name, string Slug)>());

        var auditLog = Substitute.For<IAuditLogService>();
        auditLog.GetAuditLogPageAsync(null, 1, 50, Arg.Any<CancellationToken>())
            .Returns(page);

        var service = new AuditViewerService(auditLog);

        var result = await service.GetPageAsync(null, 1, 50);

        result.TotalCount.Should().Be(1);
        result.Items.Should().HaveCount(1);
        result.Items[0].ActorDisplayName.Should().Be("Frank");
    }

    private static IAuditLogService MakeAuditLog(
        AuditLogEntry entry, Guid actor, Guid viewer, string actorName, string viewerName)
    {
        var auditLog = Substitute.For<IAuditLogService>();
        auditLog.GetByUserAsync(viewer, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { entry });
        auditLog.GetUserDisplayNamesAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>
            {
                [actor] = actorName,
                [viewer] = viewerName
            });
        auditLog.GetTeamNamesAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, (string Name, string Slug)>());
        return auditLog;
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
