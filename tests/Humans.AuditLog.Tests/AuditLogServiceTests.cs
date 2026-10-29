using Humans.AuditLog.Data;
using Humans.AuditLog.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Humans.AuditLog.Domain;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Humans.AuditLog.Contracts;
using Humans.AuditLog.Services;
using Humans.Users.Contracts;
using Xunit;

namespace Humans.AuditLog.Tests;

public class AuditLogServiceTests : IDisposable
{
    private readonly AuditLogDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly IUserService _userService;
    private readonly AuditLogService _service;

    public AuditLogServiceTests()
    {
        var options = new DbContextOptionsBuilder<AuditLogDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new AuditLogDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        var repo = new AuditLogRepository(new TestDbContextFactory<AuditLogDbContext>(options));
        _userService = Substitute.For<IUserService>();
        // Default: nothing merged into anybody.
        _service = new AuditLogService(repo, _userService, _clock, NullLogger<AuditLogService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task AbandonedAuditPage_CancelsRepositoryReads()
    {
        using var request = new CancellationTokenSource();
        var controller = new AuditLogController(_userService, new AuditViewerService(_service, []))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestAborted = request.Token }
            }
        };
        (await controller.Index(null)).Should().BeOfType<ViewResult>();
        await request.CancelAsync();
        Func<Task> read = async () => await controller.Index(null);
        await read.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansFact]
    public async Task LogAsync_JobOverload_PersistsEntry()
    {
        var entityId = Guid.NewGuid();

        await _service.LogAsync(
            AuditAction.VolunteerApproved, nameof(User), entityId,
            "Auto-approved", "SystemTeamSyncJob");

        var entry = await _dbContext.AuditLogEntries.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        entry.Action.Should().Be(AuditAction.VolunteerApproved);
        entry.EntityType.Should().Be("User");
        entry.EntityId.Should().Be(entityId);
        entry.Description.Should().Be("SystemTeamSyncJob: Auto-approved");
        entry.ActorUserId.Should().BeNull();
        entry.OccurredAt.Should().Be(_clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task LogAsync_HumanOverload_PersistsEntryWithActorFields()
    {
        var entityId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        await _service.LogAsync(
            AuditAction.MemberSuspended, nameof(User), entityId,
            "Suspended for inactivity", actorId);

        var entry = await _dbContext.AuditLogEntries.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        entry.ActorUserId.Should().Be(actorId);
        entry.Action.Should().Be(AuditAction.MemberSuspended);
        entry.EntityType.Should().Be("User");
        entry.EntityId.Should().Be(entityId);
        entry.Description.Should().Be("Suspended for inactivity");
        entry.OccurredAt.Should().Be(_clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task LogAsync_PersistsImmediatelyWithoutCallerSaveChanges()
    {
        var entityId = Guid.NewGuid();

        await _service.LogAsync(
            AuditAction.RoleAssigned, nameof(User), entityId,
            "Assigned Board role", "TestJob");

        // Each entry is persisted immediately through the repository; the caller never calls SaveChanges.
        (await _dbContext.AuditLogEntries.AsNoTracking().CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogAsync_PreservesFullDescriptionWithinItsStorageColumn(bool systemAction)
    {
        // A valid 4000-character Workgroup disposition note gains an action/title prefix.
        var description = $"Recorded Accepted on 'Proposal': {new string('x', 4000)}";
        if (systemAction)
            await _service.LogAsync(AuditAction.WorkgroupDispositionRecorded, "WorkgroupDocument",
                Guid.NewGuid(), description, "TestJob");
        else
            await _service.LogAsync(AuditAction.WorkgroupDispositionRecorded, "WorkgroupDocument",
                Guid.NewGuid(), description, Guid.NewGuid());

        var entry = await _dbContext.AuditLogEntries.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        entry.Description.Should().Be(systemAction ? $"TestJob: {description}" : description);
        // InMemory does not enforce varchar bounds; verify the complete entry fits the real mapping.
        var mapping = _dbContext.Model.FindEntityType(typeof(AuditLogEntry))!;
        entry.Description.Length.Should().BeLessThanOrEqualTo(
            mapping.FindProperty(nameof(AuditLogEntry.Description))!.GetMaxLength() ?? int.MaxValue);
    }

    [HumansFact]
    public async Task GetFilteredAsync_NoFilter_ReturnsAllWithCorrectTotalCount()
    {
        var now = _clock.GetCurrentInstant();
        SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", Guid.NewGuid(), now - Duration.FromHours(2));
        SeedAuditLogEntry(AuditAction.MemberSuspended, "User", Guid.NewGuid(), now - Duration.FromHours(1));
        SeedAuditLogEntry(AuditAction.RoleAssigned, "User", Guid.NewGuid(), now);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (items, totalCount, _) = await _service.GetFilteredAsync(null, 1, 10, TestContext.Current.CancellationToken);

        items.Should().HaveCount(3);
        totalCount.Should().Be(3);
    }

    [HumansFact]
    public async Task GetFilteredAsync_FiltersByAction()
    {
        var now = _clock.GetCurrentInstant();
        SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", Guid.NewGuid(), now - Duration.FromHours(2));
        SeedAuditLogEntry(AuditAction.MemberSuspended, "User", Guid.NewGuid(), now - Duration.FromHours(1));
        SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", Guid.NewGuid(), now);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (items, totalCount, _) = await _service.GetFilteredAsync("VolunteerApproved", 1, 10, TestContext.Current.CancellationToken);

        items.Should().HaveCount(2);
        totalCount.Should().Be(2);
        items.Should().OnlyContain(e => e.Action == AuditAction.VolunteerApproved);
    }

    [HumansFact]
    public async Task GetFilteredAsync_ReturnsAnomalyCount()
    {
        var now = _clock.GetCurrentInstant();
        SeedAuditLogEntry(AuditAction.AnomalousPermissionDetected, "GoogleResource", Guid.NewGuid(), now);
        SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", Guid.NewGuid(), now - Duration.FromHours(1));
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (items, totalCount, anomalyCount) = await _service.GetFilteredAsync("VolunteerApproved", 1, 10, TestContext.Current.CancellationToken);

        items.Should().HaveCount(1);
        totalCount.Should().Be(1);
        anomalyCount.Should().Be(1);
    }

    [HumansFact]
    public async Task GetFilteredAsync_Pagination()
    {
        var now = _clock.GetCurrentInstant();
        for (var i = 0; i < 5; i++)
        {
            SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", Guid.NewGuid(),
                now - Duration.FromHours(i));
        }
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (items, totalCount, _) = await _service.GetFilteredAsync(null, 2, 2, TestContext.Current.CancellationToken);

        items.Should().HaveCount(2);
        totalCount.Should().Be(5);
    }

    [HumansFact]
    public async Task GetByUserAsync_MatchesEntityIdOrRelatedEntityId()
    {
        var userId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", userId,
            now - Duration.FromHours(1));
        SeedAuditLogEntry(AuditAction.RoleAssigned, "Team", Guid.NewGuid(),
            now, relatedEntityId: userId);
        SeedAuditLogEntry(AuditAction.MemberSuspended, "User", Guid.NewGuid(),
            now - Duration.FromHours(2));
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetByUserAsync(userId, 10, TestContext.Current.CancellationToken);

        result.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task GetByUserAsync_RespectsCountLimit()
    {
        var userId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        for (var i = 0; i < 5; i++)
        {
            SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", userId,
                now - Duration.FromHours(i));
        }
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetByUserAsync(userId, 3, TestContext.Current.CancellationToken);

        result.Should().HaveCount(3);
    }

    [HumansFact]
    public async Task GetByUserAsync_OrdersByOccurredAtDesc()
    {
        var userId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        var older = SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", userId,
            now - Duration.FromHours(2));
        var middle = SeedAuditLogEntry(AuditAction.MemberSuspended, "User", userId,
            now - Duration.FromHours(1));
        var newest = SeedAuditLogEntry(AuditAction.RoleAssigned, "User", userId, now);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetByUserAsync(userId, 10, TestContext.Current.CancellationToken);

        result.Should().HaveCount(3);
        result[0].Id.Should().Be(newest.Id);
        result[1].Id.Should().Be(middle.Id);
        result[2].Id.Should().Be(older.Id);
    }

    [HumansFact]
    public async Task LogAsync_SwallowsRepoFailure_DoesNotThrow()
    {
        var throwingRepo = Substitute.For<IAuditLogRepository>();
        throwingRepo.AddAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB unavailable"));

        using var loggerFactory = new LoggerFactory();
        var logger = loggerFactory.CreateLogger<AuditLogService>();

        var svc = new AuditLogService(
            throwingRepo, _userService, _clock, logger);

        var act = () => svc.LogAsync(
            AuditAction.VolunteerApproved, nameof(User), Guid.NewGuid(),
            "Auto-approved", "TestJob");

        await act.Should().NotThrowAsync(
            because: "audit failures are best-effort and must never propagate to the caller");
    }

    [HumansFact]
    public async Task LogAsync_SwallowsRepoFailure_LogsAtErrorLevel()
    {
        var throwingRepo = Substitute.For<IAuditLogRepository>();
        throwingRepo.AddAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB unavailable"));

        var capturing = new CapturingLogger<AuditLogService>();
        var svc = new AuditLogService(
            throwingRepo, _userService, _clock, capturing);

        await svc.LogAsync(
            AuditAction.MemberSuspended, nameof(User), Guid.NewGuid(),
            "Suspended", "TestJob");

        capturing.Entries.Should().Contain(
            e => e.Level == LogLevel.Error,
            because: "a repo save failure must be logged at Error level per design-rules §7a");
    }

    [HumansFact]
    public async Task GetByUserAsync_SurfacesMergedSourceRows_ForFoldTarget()
    {
        var targetId = Guid.NewGuid();
        var source1 = Guid.NewGuid();
        var source2 = Guid.NewGuid();
        var unrelated = Guid.NewGuid();

        _userService.GetUserInfoAsync(targetId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(WithMergedIds(targetId, source1, source2)));

        var now = _clock.GetCurrentInstant();

        SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", source1, now - Duration.FromHours(3));
        SeedAuditLogEntry(AuditAction.MemberSuspended, "User", source2, now - Duration.FromHours(2));
        SeedAuditLogEntry(AuditAction.RoleAssigned, "User", targetId, now - Duration.FromHours(1));
        SeedAuditLogEntry(AuditAction.RoleEnded, "User", unrelated, now);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetByUserAsync(targetId, 10, TestContext.Current.CancellationToken);

        result.Should().HaveCount(3,
            because: "GetByUserAsync chain-follows merge tombstones so source rows are visible on the fold target");
        result.Should().NotContain(e => e.EntityId == unrelated,
            because: "rows belonging to unrelated users must not bleed into the merged view");
    }

    [HumansFact]
    public async Task GetByUserAsync_ThroughAMergedAwayId_SurfacesTheSurvivorsRowsToo()
    {
        // The read through the archived id answers with the survivor's record, and the
        // ids unioned are that record's own (survivor first), so rows written against the
        // survivor after the merge show up beside the archived id's own history.
        var archived = Guid.NewGuid();
        var survivor = Guid.NewGuid();
        var resolved = WithMergedIds(survivor, archived);
        _userService.GetUserInfoAsync(archived, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(resolved));

        var now = _clock.GetCurrentInstant();
        SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", archived, now - Duration.FromHours(2));
        SeedAuditLogEntry(AuditAction.RoleAssigned, "User", survivor, now - Duration.FromHours(1));
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetByUserAsync(archived, 10, TestContext.Current.CancellationToken);

        result.Select(e => e.EntityId).Should().BeEquivalentTo([archived, survivor]);
    }

    [HumansFact]
    public async Task GetFilteredEntriesAsync_UserFilter_FollowsMergedIds()
    {
        var userId = Guid.NewGuid();
        var source = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(WithMergedIds(userId, source)));

        var now = _clock.GetCurrentInstant();
        SeedAuditLogEntry(AuditAction.RoleAssigned, "Team", Guid.NewGuid(), now, actorUserId: source);
        SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", userId, now - Duration.FromHours(1));
        SeedAuditLogEntry(AuditAction.RoleEnded, "User", Guid.NewGuid(), now - Duration.FromHours(2));
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetFilteredEntriesAsync(
            userId: userId, ct: TestContext.Current.CancellationToken);

        result.Select(e => e.Action).Should().Equal(AuditAction.RoleAssigned, AuditAction.VolunteerApproved);
    }

    [HumansFact]
    public async Task ContributeForUserAsync_ExportsActorEntityAndRelatedRows_AcrossMergedIds_AsTheSamePerson()
    {
        var userId = Guid.NewGuid();
        var source = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(WithMergedIds(userId, source)));

        var now = _clock.GetCurrentInstant();
        SeedAuditLogEntry(AuditAction.RoleAssigned, "Team", Guid.NewGuid(), now, actorUserId: userId);
        SeedAuditLogEntry(AuditAction.VolunteerApproved, "User", source, now - Duration.FromHours(1));
        SeedAuditLogEntry(AuditAction.TeamMemberAdded, "Team", Guid.NewGuid(), now - Duration.FromHours(2),
            relatedEntityId: userId);
        SeedAuditLogEntry(AuditAction.MemberSuspended, "User", Guid.NewGuid(), now - Duration.FromHours(3),
            actorUserId: source);
        SeedAuditLogEntry(AuditAction.RoleEnded, "User", Guid.NewGuid(), now - Duration.FromHours(4));
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var slice = (await _service.ContributeForUserAsync(userId, TestContext.Current.CancellationToken)).Single();

        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(slice.Data));
        var rows = json.RootElement.EnumerateArray()
            .Select(r => (Action: (AuditAction)r.GetProperty("Action").GetInt32(), Role: r.GetProperty("Role").GetString()))
            .ToList();
        rows.Should().BeEquivalentTo(
        [
            (Action: AuditAction.RoleAssigned, Role: "Actor"),
            (Action: AuditAction.VolunteerApproved, Role: "Subject"),
            (Action: AuditAction.TeamMemberAdded, Role: "Subject"),
            (Action: AuditAction.MemberSuspended, Role: "Actor")
        ]);
    }

    private AuditLogEntry SeedAuditLogEntry(
        AuditAction action, string entityType, Guid entityId, Instant occurredAt,
        Guid? relatedEntityId = null, Guid? actorUserId = null)
    {
        var entry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Description = $"Test {action}",
            OccurredAt = occurredAt,
            ActorUserId = actorUserId,
            RelatedEntityId = relatedEntityId
        };
        _dbContext.AuditLogEntries.Add(entry);
        return entry;
    }

    /// <summary>A record listing the given ids as accounts merged into <paramref name="userId"/>.</summary>
    private static UserInfo WithMergedIds(Guid userId, params Guid[] mergedUserIds)
    {
        var info = UserInfo.Create(new User { Id = userId, PreferredLanguage = "en" }, [], [], [], null, []);
        return info with { MergedUserIds = mergedUserIds };
    }
}
