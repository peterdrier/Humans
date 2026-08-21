using Humans.AuditLog.Services;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.AuditLog.Tests.Infrastructure;
using Humans.Base.Interfaces;
using NodaTime;
using NSubstitute;

namespace Humans.AuditLog.Tests;

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
            entityType: "ShiftSignup",
            entityId: Guid.NewGuid(),
            relatedEntityId: viewer,
            relatedEntityType: "User",
            description: "shift 'Cantina'");

        var auditLog = Substitute.For<IAuditLogReader>();
        auditLog.GetByUserAsync(viewer, 10, Arg.Any<CancellationToken>())
            .Returns([entry]);

        var service = new AuditViewerService(
            auditLog, [StubContributor("User", (actor, "Frank"), (viewer, "Peter"))]);

        var events = await service.GetForUserAsync(viewer, 10, Xunit.TestContext.Current.CancellationToken);

        events.Should().HaveCount(1);
        var ev = events[0];
        ev.ActorDisplayName.Should().Be("Frank");
        ev.SubjectUserId.Should().Be(viewer);
        ev.SubjectDisplayName.Should().Be("Peter");
    }

    [HumansFact]
    public async Task GetForUserAsync_EmptyResult_ReturnsEmptyList()
    {
        var auditLog = Substitute.For<IAuditLogReader>();
        auditLog.GetByUserAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var contributor = Substitute.For<IEntityNameContributor>();
        var service = new AuditViewerService(auditLog, [contributor]);

        var events = await service.GetForUserAsync(Guid.NewGuid(), 10, Xunit.TestContext.Current.CancellationToken);

        events.Should().BeEmpty();
        // No name lookups should fire on empty input — short-circuit guard.
        await contributor.DidNotReceive().ResolveNamesAsync(
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
            entityType: "ShiftSignup",
            entityId: Guid.NewGuid(),
            relatedEntityId: viewer,
            relatedEntityType: "User",
            description: "shift 'Cantina dinner'");

        var auditLog = Substitute.For<IAuditLogReader>();
        auditLog.GetByUserAsync(viewer, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([entry]);

        var service = new AuditViewerService(
            auditLog, [StubContributor("User", (actor, "Frank"), (viewer, "Peter"))]);

        var events = await service.GetForUserAsync(viewer, 10, Xunit.TestContext.Current.CancellationToken);
        var line = events[0].RenderPlainText(viewerUserId: viewer);

        line.Should().Be("2026-04-30 — Frank voluntold You — shift 'Cantina dinner'");
        line.Should().NotContain(viewer.ToString());
        line.Should().NotContain(actor.ToString());
    }

    [HumansFact]
    public async Task GetForUserAsync_ResolvesTeamNameAndSlugFromTheTeamContributor()
    {
        var viewer = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var entry = MakeEntry(
            action: AuditAction.RoleAssigned,
            actorId: viewer,
            entityType: "Team",
            entityId: teamId,
            description: "Assigned");

        var auditLog = Substitute.For<IAuditLogReader>();
        auditLog.GetByUserAsync(viewer, 10, Arg.Any<CancellationToken>())
            .Returns([entry]);

        var teams = Substitute.For<IEntityNameContributor>();
        teams.ResolveNamesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, EntityName>
            {
                [teamId] = new("Team", "Cantina", "cantina")
            });

        var service = new AuditViewerService(
            auditLog, [StubContributor("User", (viewer, "Peter")), teams]);

        var events = await service.GetForUserAsync(viewer, 10, Xunit.TestContext.Current.CancellationToken);

        events[0].TargetTeamId.Should().Be(teamId);
        events[0].TargetTeamName.Should().Be("Cantina");
        events[0].TargetTeamSlug.Should().Be("cantina");
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

        var auditLog = Substitute.For<IAuditLogReader>();
        auditLog.GetFilteredAsync(null, 1, 50, Arg.Any<CancellationToken>())
            .Returns(([entry], 1, 0));

        var service = new AuditViewerService(auditLog, [StubContributor("User", (actor, "Frank"))]);

        var result = await service.GetPageAsync(null, 1, 50, Xunit.TestContext.Current.CancellationToken);

        result.TotalCount.Should().Be(1);
        result.Items.Should().HaveCount(1);
        result.Items[0].ActorDisplayName.Should().Be("Frank");
    }

    private static IEntityNameContributor StubContributor(
        string entityType, params (Guid Id, string Name)[] entries)
    {
        var contributor = Substitute.For<IEntityNameContributor>();
        var dict = entries.ToDictionary(e => e.Id, e => new EntityName(entityType, e.Name));
        contributor.ResolveNamesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<Guid, EntityName>)dict);
        return contributor;
    }

    private static AuditLogEntrySnapshot MakeEntry(
        AuditAction action,
        Guid? actorId,
        string entityType,
        Guid entityId,
        Guid? relatedEntityId = null,
        string? relatedEntityType = null,
        string description = "") =>
        new(
            Guid.NewGuid(),
            action,
            entityType,
            entityId,
            description,
            FixedAt,
            actorId,
            relatedEntityId,
            relatedEntityType);
}
