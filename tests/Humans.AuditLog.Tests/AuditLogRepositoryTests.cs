using System.Transactions;
using NSubstitute;
using Xunit;
using Humans.AuditLog.Data;
using Humans.AuditLog.Domain;
using Humans.AuditLog.Services;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.AuditLog.Contracts;

namespace Humans.AuditLog.Tests;

public class AuditLogRepositoryTests
{
    private readonly IDbContextFactory<AuditLogDbContext> _factory;
    private readonly AuditLogRepository _sut;

    public AuditLogRepositoryTests()
    {
        var options = TestDbOptions.InMemory<AuditLogDbContext>();
        _factory = new TestDbContextFactory<AuditLogDbContext>(options);
        _sut = new AuditLogRepository(_factory);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddAsync_DoesNotEnlistInTheCallersTransaction(bool callerCommits)
    {
        var entry = MakeEntry(TestActions.VolunteerApproved, "User", Guid.NewGuid());
        var factory = Substitute.For<IDbContextFactory<AuditLogDbContext>>();
        string? auditTransactionId = null;
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            auditTransactionId = Transaction.Current?.TransactionInformation.LocalIdentifier;
            return _factory.CreateDbContextAsync(call.Arg<CancellationToken>());
        });
        var repository = new AuditLogRepository(factory);
        Transaction transaction;
        using (var scope = new TransactionScope(TransactionScopeOption.Required,
            TransactionScopeAsyncFlowOption.Enabled))
        {
            transaction = Transaction.Current!.Clone();
            await repository.AddAsync(entry, TestContext.Current.CancellationToken);
            Transaction.Current.Should().NotBeNull();
            Transaction.Current.TransactionInformation.LocalIdentifier.Should().Be(transaction.TransactionInformation.LocalIdentifier);
            if (callerCommits) scope.Complete();
        }
        using (transaction)
        {
            auditTransactionId.Should().BeNull("audit saves must commit independently of the caller's business transaction");
            transaction.TransactionInformation.Status.Should().Be(callerCommits ? TransactionStatus.Committed : TransactionStatus.Aborted);
        }
        await factory.Received(1).CreateDbContextAsync(TestContext.Current.CancellationToken);
        await using var context = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        (await context.AuditLogEntries.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).Id.Should().Be(entry.Id);
    }

    [HumansFact]
    public async Task GetFilteredAsync_LargePageDoesNotWrapToEarlierEntries()
    {
        await _sut.AddAsync(MakeEntry(TestActions.VolunteerApproved, "User", Guid.NewGuid()), TestContext.Current.CancellationToken);
        var first = await _sut.GetFilteredAsync(null, [], 1, 50, TestContext.Current.CancellationToken);
        first.Items.Should().ContainSingle();
        var result = await _sut.GetFilteredAsync(null, [], int.MaxValue, 50, TestContext.Current.CancellationToken);
        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(1);
        result.AlertCounts.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetFilteredAsync_CountsEveryAlertActionOverTheWholeLog()
    {
        var ct = TestContext.Current.CancellationToken;
        await _sut.AddAsync(MakeEntry(TestActions.AnomalousPermissionDetected, "GoogleResource", Guid.NewGuid()), ct);
        await _sut.AddAsync(MakeEntry(TestActions.AnomalousPermissionDetected, "GoogleResource", Guid.NewGuid()), ct);
        await _sut.AddAsync(MakeEntry(TestActions.MemberSuspended, "User", Guid.NewGuid()), ct);

        var result = await _sut.GetFilteredAsync(
            TestActions.MemberSuspended.Name,
            [TestActions.AnomalousPermissionDetected.Name, TestActions.MemberSuspended.Name],
            1, 50, ct);

        result.Items.Should().ContainSingle();
        result.AlertCounts.Should().BeEquivalentTo(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [TestActions.AnomalousPermissionDetected.Name] = 2,
            [TestActions.MemberSuspended.Name] = 1,
        });
    }

    [HumansFact]
    public async Task AddAsync_PersistsEntry_VisibleOnNextRead()
    {
        var entry = MakeEntry(TestActions.VolunteerApproved, "User", Guid.NewGuid());

        await _sut.AddAsync(entry, TestContext.Current.CancellationToken);

        await using var ctx = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var stored = await ctx.AuditLogEntries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == entry.Id, TestContext.Current.CancellationToken);

        stored.Should().NotBeNull(
            because: "AddAsync is the sole write path — it must persist immediately without a caller SaveChanges");
        stored.Action.Should().Be(entry.Action);
        stored.EntityType.Should().Be(entry.EntityType);
        stored.EntityId.Should().Be(entry.EntityId);
    }

    [HumansFact]
    public async Task GetLegacyGoogleSyncEntriesAsync_ReturnsOnlyResourceRows_Oldest_First()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Instant.FromUtc(2026, 5, 12, 10, 0);

        await _sut.AddAsync(MakeEntry(TestActions.VolunteerApproved, "User", Guid.NewGuid()), ct);
        var newer = MakeGoogleSyncEntry(now);
        var older = MakeGoogleSyncEntry(now - Duration.FromHours(3));
        await using (var seed = await _factory.CreateDbContextAsync(ct))
        {
            seed.AuditLogEntries.AddRange(newer, older);
            await seed.SaveChangesAsync(ct);
        }

        var result = await _sut.GetLegacyGoogleSyncEntriesAsync(ct);

        // Chronological, and the non-Google row is absent — the one-time migration reads the whole set.
        result.Select(e => e.Id).Should().Equal([older.Id, newer.Id]);
    }

    [HumansFact]
    public async Task AddAsync_RetentionAction_StoresOnTemporaryTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var entry = MakeEntry(TestActions.BudgetLineChanged, "BudgetLine", Guid.NewGuid());

        await _sut.AddAsync(entry, ct);

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        (await ctx.TemporaryAuditLogEntries.AsNoTracking().SingleAsync(ct)).Id.Should().Be(entry.Id);
        (await ctx.AuditLogEntries.AnyAsync(ct)).Should().BeFalse();
    }

    [HumansFact]
    public async Task GetFilteredEntriesAsync_BothSides_MergedNewestFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        var lineId = Guid.NewGuid();
        var day = Instant.FromUtc(2026, 5, 12, 0, 0);
        var permanent = MakeEntry(TestActions.VolunteerApproved, "BudgetLine", lineId, day);
        var temporary = MakeEntry(TestActions.BudgetLineChanged, "BudgetLine", lineId, day + Duration.FromHours(1));
        await _sut.AddAsync(permanent, ct);
        await _sut.AddAsync(temporary, ct);

        var result = await _sut.GetFilteredEntriesAsync("BudgetLine", lineId, null, null, 10, ct: ct);

        result.Select(e => (e.Id, e.Kept)).Should().Equal(
            (temporary.Id, AuditKept.RetentionPeriod), (permanent.Id, AuditKept.Forever));
    }

    [HumansFact]
    public async Task DeleteTemporaryBeforeAsync_OldPermanentRow_Kept()
    {
        var ct = TestContext.Current.CancellationToken;
        var cutoff = Instant.FromUtc(2026, 5, 12, 0, 0);
        var oldPermanent = MakeEntry(TestActions.VolunteerApproved, "User", Guid.NewGuid(), cutoff - Duration.FromDays(1));
        var oldTemporary = MakeEntry(TestActions.BudgetLineChanged, "BudgetLine", Guid.NewGuid(), cutoff - Duration.FromDays(1));
        var newTemporary = MakeEntry(TestActions.BudgetLineChanged, "BudgetLine", Guid.NewGuid(), cutoff);
        foreach (var e in new[] { oldPermanent, oldTemporary, newTemporary })
            await _sut.AddAsync(e, ct);

        var deleted = await _sut.DeleteTemporaryBeforeAsync(cutoff, ct);

        deleted.Should().Be(1);
        var left = await _sut.GetFilteredEntriesAsync(null, null, null, null, 10, ct: ct);
        left.Select(e => e.Id).Should().BeEquivalentTo([oldPermanent.Id, newTemporary.Id]);
    }

    [HumansFact]
    public async Task DeleteTemporaryForUserAsync_OnlyThatUsersTemporaryRows()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = Guid.NewGuid();
        var mine = MakeEntry(TestActions.BudgetLineChanged, "BudgetLine", Guid.NewGuid(), actorId: userId);
        var others = MakeEntry(TestActions.BudgetLineChanged, "BudgetLine", Guid.NewGuid(), actorId: Guid.NewGuid());
        var minePermanent = MakeEntry(TestActions.VolunteerApproved, "User", userId);
        foreach (var e in new[] { mine, others, minePermanent })
            await _sut.AddAsync(e, ct);

        var deleted = await _sut.DeleteTemporaryForUserAsync(userId, ct);

        deleted.Should().Be(1);
        var left = await _sut.GetFilteredEntriesAsync(null, null, null, null, 10, ct: ct);
        left.Select(e => e.Id).Should().BeEquivalentTo([others.Id, minePermanent.Id]);
    }

    private static AuditLogEntry MakeGoogleSyncEntry(Instant occurredAt) => new()
    {
        Id = Guid.NewGuid(),
        Action = TestActions.GoogleResourceAccessGranted.Name,
        EntityType = "GoogleResource",
        EntityId = Guid.NewGuid(),
        Description = "GoogleWorkspaceSyncService: Granted Drive access",
        OccurredAt = occurredAt,
        ResourceId = Guid.NewGuid(),
        Success = true,
        Role = "writer",
        SyncSource = Base.Enums.GoogleSyncSource.ManualSync,
        UserEmail = "a@b.org"
    };

    private static AuditLogEntrySnapshot MakeEntry(
        AuditActionDefinition action, string entityType, Guid entityId,
        Instant? occurredAt = null, Guid? actorId = null) =>
        new(Guid.NewGuid(), action.Name, entityType, entityId, $"Test {action.Name}",
            occurredAt ?? Instant.FromUtc(2026, 5, 12, 0, 0), actorId, null, null, action.Kept);
}
