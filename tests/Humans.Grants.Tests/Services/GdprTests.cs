using System.Transactions;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Grants.Services;
using Humans.Grants.Data;
using Humans.Grants.Tests.Infrastructure;
using Humans.Users.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NSubstitute;

namespace Humans.Grants.Tests.Services;

/// <summary>Export, erasure, and merge.</summary>
public sealed class GdprTests : GrantsTestHarness
{
    [HumansFact]
    public void ErasureDeclaration_CoversBothSlices()
    {
        IUserDataContributor contributor = NewService();
        contributor.ErasureDeclaration.Keys.Should().BeEquivalentTo([GrantService.Grants, GrantService.GrantDecisions]);
        contributor.ErasureDeclaration[GrantService.Grants].Should().BeNull();
        contributor.ErasureDeclaration[GrantService.GrantDecisions].Should().Contain("Art. 6(1)(f)");
    }

    [HumansFact]
    public async Task Export_GrantsAlwaysAList_DecisionsNullWhenNone()
    {
        var person = SeedUser("Person");
        await SeedGrantAsync(await SeedProgramAsync(), person);
        IUserDataContributor contributor = NewService();

        var slices = await contributor.ContributeForUserAsync(person, Ct);

        slices.Select(s => s.SectionName).Should().Equal(GrantService.Grants, GrantService.GrantDecisions);
        ((System.Collections.IEnumerable)slices[0].Data!).Cast<object>().Should().HaveCount(1);
        slices[1].Data.Should().BeNull();

        var nobody = await contributor.ContributeForUserAsync(SeedUser("Nobody"), Ct);
        ((System.Collections.IEnumerable)nobody[0].Data!).Cast<object>().Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData(true, 2028)]
    [Xunit.InlineData(false, 2027)]
    public async Task Export_UsesTheProgramsEventYear_WithStoredYearFallback(bool eventAvailable, int expectedYear)
    {
        var person = SeedUser("Holder and creator");
        var program = await SeedProgramAsync();
        await using (var db = OpenContext())
        {
            var row = await db.Programs.SingleAsync(Ct);
            row.EventSettingsId = Event2027.Id;
            row.CreatedByUserId = person;
            await db.SaveChangesAsync(Ct);
        }
        await SeedGrantAsync(program, person);
        var movedEvent = BurnFixtures.Burn(year: 2028) with { Id = Event2027.Id };
        Settings.GetAllEventSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(eventAvailable ? [movedEvent] : []);

        var slices = await NewService().ContributeForUserAsync(person, Ct);

        var grants = System.Text.Json.JsonSerializer.SerializeToElement(slices[0].Data);
        grants.EnumerateArray().Single().GetProperty("Year").GetInt32().Should().Be(expectedYear);
        var decisions = System.Text.Json.JsonSerializer.SerializeToElement(slices[1].Data);
        decisions.GetProperty("ProgramsCreated").EnumerateArray().Single()
            .GetProperty("Year").GetInt32().Should().Be(expectedYear);
    }

    [HumansFact]
    public async Task Export_DecisionsSlice_ForManagerAndIssuer()
    {
        var manager = SeedUser("Manager");
        var program = await SeedProgramAsync(managers: manager);
        (await NewService().IssueToAsync(program.Id, [SeedUser()], null, manager, Ct)).Refusal.Should().BeNull();

        var slices = await NewService().ContributeForUserAsync(manager, Ct);

        var text = System.Text.Json.JsonSerializer.Serialize(slices[1].Data);
        text.Should().Contain("\"ManagerOf\":[{").And.Contain("\"GrantsIssued\":[{");
    }

    [HumansFact]
    public async Task Export_DecisionsSlice_ListsManagersTheyNamed_ErasureKeepsTheActorId()
    {
        var program = await SeedProgramAsync(managers: SeedUser("Manager"));
        Guid namer;
        await using (var db = OpenContext())
            namer = (await db.Managers.SingleAsync(Ct)).AddedByUserId;

        var slices = await NewService().ContributeForUserAsync(namer, Ct);
        System.Text.Json.JsonSerializer.Serialize(slices[1].Data)
            .Should().Contain("\"ManagersNamed\":[{\"Program\":\"" + program.Name + "\"");

        await NewService().EraseForUserAsync(namer, Ct);
        await using var read = OpenContext();
        (await read.Managers.SingleAsync(Ct)).AddedByUserId.Should().Be(namer);
        NewService().ErasureDeclaration[GrantService.GrantDecisions].Should().Contain("managers they named");
    }

    [HumansFact]
    public async Task Erase_DeletesTheirGrantsAndManagerRows_KeepsGrantsTheyIssued()
    {
        var person = SeedUser("Person");
        var program = await SeedProgramAsync(managers: person);
        await SeedGrantAsync(program, person);
        await SeedGrantAsync(program, person, revoked: true);
        var other = await SeedGrantAsync(program, SeedUser("Other"));
        await using (var db = OpenContext())
        {
            (await db.Grants.SingleAsync(g => g.Id == other.Id, Ct)).IssuedByUserId = person;
            await db.SaveChangesAsync(Ct);
        }

        await NewService().EraseForUserAsync(person, Ct);

        await using var read = OpenContext();
        (await read.Grants.Select(g => g.Id).ToListAsync(Ct)).Should().Equal(other.Id);
        (await read.Managers.CountAsync(Ct)).Should().Be(0);
    }

    [HumansFact]
    public async Task Merge_ReKeysAManagerRowTheMergedAccountBothHoldsAndAdded()
    {
        var from = SeedUser("From");
        var to = SeedUser("To");
        var program = await SeedProgramAsync(managers: from);
        await using (var db = OpenContext())
        {
            (await db.Managers.SingleAsync(Ct)).AddedByUserId = from;
            await db.SaveChangesAsync(Ct);
        }

        IUserMerge merge = NewService();
        await merge.ReassignAsync(from, to, SeedUser("Admin"), Clock.GetCurrentInstant(), Ct);

        await using var read = OpenContext();
        var row = await read.Managers.SingleAsync(Ct);
        (row.ProgramId, row.UserId, row.AddedByUserId).Should().Be((program.Id, to, to));
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Merge_StagesShareATransaction_ThatCommitsOnlyWhenBothSucceed(bool failSecondSave)
    {
        var from = SeedUser("From");
        var to = SeedUser("To");
        var actor = SeedUser("Admin");
        var program = await SeedProgramAsync(managers: from);
        await SeedGrantAsync(program, to);
        await SeedGrantAsync(program, from);
        var probe = new MergeTransactionProbe(failSecondSave);
        var options = new DbContextOptionsBuilder<GrantsDbContext>(
                (DbContextOptions<GrantsDbContext>)Db.GetService<IDbContextOptions>())
            .AddInterceptors(probe).Options;
        var service = new GrantService(
            new GrantRepository(new TestDbContextFactory<GrantsDbContext>(options)),
            Users, UserEmails, Notifications, Email, EmailFactory, AuditLog, Settings, Clock, Logger);

        if (failSecondSave)
        {
            Func<Task> merge = () => service.ReassignAsync(from, to, actor, Clock.GetCurrentInstant(), Ct);
            await merge.Should().ThrowAsync<IOException>();
            AuditLog.ReceivedCalls().Should().BeEmpty();
        }
        else
        {
            await service.ReassignAsync(from, to, actor, Clock.GetCurrentInstant(), Ct);
        }

        probe.TransactionIds.Should().HaveCount(2).And.NotContainNulls();
        probe.TransactionIds.Distinct(StringComparer.Ordinal).Should().ContainSingle();
        probe.CompletedStatus.Should().Be(failSecondSave ? TransactionStatus.Aborted : TransactionStatus.Committed);
        Transaction.Current.Should().BeNull();
    }

    private sealed class MergeTransactionProbe(bool failSecondSave) : SaveChangesInterceptor
    {
        public List<string?> TransactionIds { get; } = [];
        public TransactionStatus? CompletedStatus { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var transaction = Transaction.Current;
            if (TransactionIds.Count == 0 && transaction is not null)
                transaction.TransactionCompleted += (_, completed) =>
                    CompletedStatus = completed.Transaction!.TransactionInformation.Status;
            TransactionIds.Add(transaction?.TransactionInformation.LocalIdentifier);
            if (failSecondSave && TransactionIds.Count == 2)
                throw new IOException("merge re-key save failed");
            return ValueTask.FromResult(result);
        }
    }

    [HumansFact]
    public async Task Merge_ReKeysGrants_RevokesTheDuplicateLiveGrant_Audits()
    {
        var from = SeedUser("From");
        var to = SeedUser("To");
        var actor = SeedUser("Admin");
        var shared = await SeedProgramAsync(name: "Shared");
        var onlyFrom = await SeedProgramAsync(name: "Only from");
        var keep = await SeedGrantAsync(shared, to);
        var duplicate = await SeedGrantAsync(shared, from);
        var moved = await SeedGrantAsync(onlyFrom, from);

        IUserMerge merge = NewService();
        await merge.ReassignAsync(from, to, actor, Clock.GetCurrentInstant(), Ct);

        var grants = await ReadGrantsAsync();
        grants.Should().OnlyContain(g => g.UserId == to);
        grants.Single(g => g.Id == keep.Id).RevokedAt.Should().BeNull();
        grants.Single(g => g.Id == moved.Id).RevokedAt.Should().BeNull();
        var revoked = grants.Single(g => g.Id == duplicate.Id);
        revoked.RevokedAt.Should().NotBeNull();
        revoked.RevokedByUserId.Should().Be(actor);
        await AuditLog.Received(1).LogAsync(GrantsAuditActions.GrantRevoked, AuditEntityTypes.Grant, duplicate.Id,
            Arg.Any<string>(), actor, to, AuditEntityTypes.User);
    }
}
