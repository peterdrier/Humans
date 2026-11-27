using Humans.Audit.Services;
using AwesomeAssertions;
using Humans.Audit.Contracts;
using Humans.Base.Interfaces;
using NodaTime;
using NSubstitute;

namespace Humans.Audit.Tests;

public class AuditViewerServiceTests
{
    private static readonly Instant FixedAt = Instant.FromUtc(2026, 4, 30, 17, 0);
    private static readonly AuditActionRegistry Registry = new([new TestActions()]);

    [HumansFact]
    public async Task GetForUserAsync_ResolvesDisplayNames()
    {
        var viewer = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var entry = MakeEntry(
            action: TestActions.ShiftSignupVoluntold,
            actorId: actor,
            subjectType: "ShiftSignup",
            subjectId: Guid.NewGuid(),
            targetId: viewer,
            targetType: "User",
            description: "shift 'Cantina'");

        var auditLog = Substitute.For<IAuditLogReader>();
        auditLog.GetByUserAsync(viewer, 10, Arg.Any<CancellationToken>())
            .Returns([entry]);

        var service = new AuditViewerService(
            auditLog, Registry, [StubContributor("User", (actor, "Frank"), (viewer, "Peter"))]);

        var entries = await service.GetForUserAsync(viewer, 10, Xunit.TestContext.Current.CancellationToken);

        entries.Should().HaveCount(1);
        var resolved = entries[0];
        resolved.Action.Should().BeSameAs(TestActions.ShiftSignupVoluntold);
        resolved.ActorDisplayName.Should().Be("Frank");
        resolved.PersonUserId.Should().Be(viewer);
        resolved.PersonDisplayName.Should().Be("Peter");
    }

    [HumansFact]
    public async Task GetForUserAsync_EmptyResult_ReturnsEmptyList()
    {
        var auditLog = Substitute.For<IAuditLogReader>();
        auditLog.GetByUserAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var contributor = Substitute.For<IEntityNameContributor>();
        var service = new AuditViewerService(auditLog, Registry, [contributor]);

        var entries = await service.GetForUserAsync(Guid.NewGuid(), 10, Xunit.TestContext.Current.CancellationToken);

        entries.Should().BeEmpty();
        // Empty input fires no name lookups.
        await contributor.DidNotReceive().ResolveNamesAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetForUserAsync_ViewerIsActor_RendersYou()
    {
        var viewer = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var entry = MakeEntry(
            action: TestActions.ShiftSignupVoluntold,
            actorId: actor,
            subjectType: "ShiftSignup",
            subjectId: Guid.NewGuid(),
            targetId: viewer,
            targetType: "User",
            description: "shift 'Cantina dinner'");

        var auditLog = Substitute.For<IAuditLogReader>();
        auditLog.GetByUserAsync(viewer, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([entry]);

        var service = new AuditViewerService(
            auditLog, Registry, [StubContributor("User", (actor, "Frank"), (viewer, "Peter"))]);

        var entries = await service.GetForUserAsync(viewer, 10, Xunit.TestContext.Current.CancellationToken);
        var line = entries[0].RenderPlainText(viewerUserId: viewer);

        line.Should().Be("2026-04-30 — Frank voluntold You — shift 'Cantina dinner'");
        line.Should().NotContain(viewer.ToString());
        line.Should().NotContain(actor.ToString());
    }

    [HumansFact]
    public async Task GetForUserAsync_TeamRow_ResolvesTeamName()
    {
        var viewer = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var entry = MakeEntry(
            action: TestActions.RoleAssigned,
            actorId: viewer,
            subjectType: "Team",
            subjectId: teamId,
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
            auditLog, Registry, [StubContributor("User", (viewer, "Peter")), teams]);

        var entries = await service.GetForUserAsync(viewer, 10, Xunit.TestContext.Current.CancellationToken);

        entries[0].TeamId.Should().Be(teamId);
        entries[0].TeamName.Should().Be("Cantina");
        entries[0].TeamSlug.Should().Be("cantina");
    }

    [HumansFact]
    public async Task GetPageAsync_ResolvesNames()
    {
        var actor = Guid.NewGuid();
        var entry = MakeEntry(
            action: TestActions.MemberSuspended,
            actorId: actor,
            subjectType: "User",
            subjectId: Guid.NewGuid(),
            description: "Suspended");

        var auditLog = Substitute.For<IAuditLogReader>();
        var alerts = new Dictionary<string, int>(StringComparer.Ordinal) { ["AnomalousPermissionDetected"] = 2 };
        auditLog.GetFilteredAsync(null, 1, 50, Arg.Any<CancellationToken>())
            .Returns(([entry], 1, alerts));

        var service = new AuditViewerService(auditLog, Registry, [StubContributor("User", (actor, "Frank"))]);

        var result = await service.GetPageAsync(null, 1, 50, Xunit.TestContext.Current.CancellationToken);

        result.TotalCount.Should().Be(1);
        result.AlertCounts.Should().BeSameAs(alerts);
        result.Items.Should().HaveCount(1);
        result.Items[0].ActorDisplayName.Should().Be("Frank");
    }

    [HumansFact]
    public async Task UndeclaredStoredName_StillRendersAsABareAction()
    {
        var entry = new AuditLogEntrySnapshot(
            Guid.NewGuid(), "RetiredLongAgo", "User", Guid.NewGuid(), "kept", FixedAt, null, null, null, AuditKept.Forever);
        var auditLog = Substitute.For<IAuditLogReader>();
        auditLog.GetByUserAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([entry]);

        var service = new AuditViewerService(auditLog, Registry, []);

        var entries = await service.GetForUserAsync(Guid.NewGuid(), 10, Xunit.TestContext.Current.CancellationToken);

        entries.Single().Action.Should().Be(new AuditActionDefinition("RetiredLongAgo", AuditKept.Forever));
    }

    private static IEntityNameContributor StubContributor(
        string subjectType, params (Guid Id, string Name)[] entries)
    {
        var contributor = Substitute.For<IEntityNameContributor>();
        var dict = entries.ToDictionary(e => e.Id, e => new EntityName(subjectType, e.Name));
        contributor.ResolveNamesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(dict);
        return contributor;
    }

    private static AuditLogEntrySnapshot MakeEntry(
        AuditActionDefinition action,
        Guid? actorId,
        string subjectType,
        Guid subjectId,
        Guid? targetId = null,
        string? targetType = null,
        string description = "") =>
        new(
            Guid.NewGuid(),
            action.Name,
            subjectType,
            subjectId,
            description,
            FixedAt,
            actorId,
            targetId,
            targetType,
            action.Kept);
}
