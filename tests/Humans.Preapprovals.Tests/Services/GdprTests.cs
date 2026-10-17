using Xunit;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Notifications.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Domain;
using Humans.Preapprovals.Services;
using Humans.Preapprovals.Tests.Infrastructure;
using Humans.Users.Contracts;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Humans.Preapprovals.Tests.Services;

/// <summary>Export, erasure, and merge.</summary>
public sealed class GdprTests : PreapprovalsTestHarness
{
    [HumansFact]
    public void ErasureDeclaration_CoversBothSlices()
    {
        IUserDataContributor contributor = NewService();
        contributor.ErasureDeclaration.Keys.Should().BeEquivalentTo([PreapprovalService.Preapprovals, PreapprovalService.PreapprovalDecisions]);
        contributor.ErasureDeclaration.Values.Should().AllSatisfy(v => v.Should().Contain("Art. 17(3)(b)"));
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_GrantsAlwaysAList_DecisionsNullWhenNone(bool requestArchivedId)
    {
        var program = await SeedProgramAsync();
        var archived = SeedUser("Archived");
        var person = SeedUser("Person", mergedIds: [archived]);
        var info = await Users.GetUserInfoAsync(person, Ct);
        Users.GetUserInfoAsync(archived, Arg.Any<CancellationToken>()).Returns(info);
        await SeedGrantAsync(program, person, label: "Fire Lotus");
        IUserDataContributor contributor = NewService();

        var slices = await contributor.ContributeForUserAsync(requestArchivedId ? archived : person, Ct);

        slices.Select(s => s.SectionName).Should().Equal(PreapprovalService.Preapprovals, PreapprovalService.PreapprovalDecisions);
        ((System.Collections.IEnumerable)slices[0].Data!).Cast<object>().Should().HaveCount(1);
        slices[1].Data.Should().BeNull();

        var nobody = await contributor.ContributeForUserAsync(SeedUser("Nobody"), Ct);
        ((System.Collections.IEnumerable)nobody[0].Data!).Cast<object>().Should().BeEmpty();
    }

    [HumansFact]
    public async Task Export_DecisionsSlice_ForManagerCreatorAndDecider()
    {
        var manager = SeedUser("Manager");
        var program = await SeedProgramAsync(managers: manager);
        await using (var db = OpenContext())
        {
            (await db.Programs.FindAsync([program.Id], Ct))!.CreatedByUserId = manager;
            await db.SaveChangesAsync(Ct);
        }
        await SeedGrantAsync(program, status: GrantStatus.Applied);
        var service = NewService();
        Guid applicationId;
        await using (var db = OpenContext()) applicationId = (await db.Grants.SingleAsync(Ct)).Id;
        (await service.ApproveAsync(applicationId, null, manager, Ct)).Refusal.Should().BeNull();

        var slices = await ((IUserDataContributor)service).ContributeForUserAsync(manager, Ct);

        var decisions = slices[1].Data.Should().NotBeNull().And.Subject;
        var text = System.Text.Json.JsonSerializer.Serialize(decisions);
        text.Should().Contain("\"ProgramsCreated\":[{").And.Contain("\"ManagerOf\":[{").And.Contain("\"GrantsDecided\":[{");
    }

    [HumansFact]
    public async Task Erase_DeletesAppliedAndManagerRows_KeepsDecidedGrants()
    {
        var person = SeedUser("Person");
        var program = await SeedProgramAsync(managers: person);
        var applied = await SeedGrantAsync(program, person, status: GrantStatus.Applied);
        var open = await SeedGrantAsync(program, person);
        var consumed = await SeedGrantAsync(program, person, status: GrantStatus.Consumed, expenseReportId: Guid.NewGuid());
        var declined = await SeedGrantAsync(program, person, status: GrantStatus.Declined);
        var someoneElse = await SeedGrantAsync(program, status: GrantStatus.Applied);

        await ((IUserDataContributor)NewService()).EraseForUserAsync(person, Ct);

        await using var db = OpenContext();
        (await db.Grants.Select(g => g.Id).ToListAsync(Ct)).Should().BeEquivalentTo([open.Id, consumed.Id, declined.Id, someoneElse.Id]);
        (await db.Managers.CountAsync(Ct)).Should().Be(0);
        (await db.Grants.FindAsync([open.Id], Ct))!.BeneficiaryUserId.Should().Be(person, "decided grants keep the beneficiary id under Art. 17(3)(b)");
    }

    [HumansFact]
    public async Task Merge_RekeysEveryColumn_DedupsManagers_CollapsesDuplicateApplications()
    {
        var from = SeedUser("Old");
        var to = SeedUser("New");
        var programA = await SeedProgramAsync(name: "A", managers: [from, to]);
        var programB = await SeedProgramAsync(name: "B", managers: from);
        var openUnderOld = await SeedGrantAsync(programA, from);
        var decidedByOld = await SeedGrantAsync(programB);
        await using (var db = OpenContext())
        {
            (await db.Grants.FindAsync([decidedByOld.Id], Ct))!.DecidedByUserId = from;
            (await db.Programs.FindAsync([programB.Id], Ct))!.CreatedByUserId = from;
            await db.SaveChangesAsync(Ct);
        }
        var olderApplication = await SeedGrantAsync(programA, to, status: GrantStatus.Applied);
        Clock.AdvanceMinutes(1);
        var newerApplication = await SeedGrantAsync(programA, from, status: GrantStatus.Applied);
        var soleApplication = await SeedGrantAsync(programB, from, status: GrantStatus.Applied);
        var actor = SeedUser("Admin");

        await ((IUserMerge)NewService()).ReassignAsync(from, to, actor, Clock.GetCurrentInstant(), Ct);

        await using var check = OpenContext();
        (await check.Grants.AnyAsync(g => g.BeneficiaryUserId == from || g.DecidedByUserId == from, Ct)).Should().BeFalse();
        (await check.Programs.AnyAsync(p => p.CreatedByUserId == from, Ct)).Should().BeFalse();
        (await check.Managers.AnyAsync(m => m.UserId == from || m.AddedByUserId == from, Ct)).Should().BeFalse();
        (await check.Managers.Where(m => m.UserId == to).Select(m => m.ProgramId).ToListAsync(Ct)).Should().BeEquivalentTo([programA.Id, programB.Id]);
        (await check.Grants.Select(g => g.Id).ToListAsync(Ct)).Should().BeEquivalentTo([openUnderOld.Id, decidedByOld.Id, olderApplication.Id, soleApplication.Id]);
        (await check.Grants.FindAsync([soleApplication.Id], Ct))!.BeneficiaryUserId.Should().Be(to);
        await NotificationResolve.Received(1).ResolveBySourceKeyAsync(NotificationSource.PreapprovalApplicationReceived,
            PreapprovalService.RequestSourceKey(newerApplication.Id), actor, Arg.Any<CancellationToken>());
        await AuditLog.Received(1).LogAsync(AuditAction.PreapprovalMergeDroppedApplication, AuditEntityTypes.Grant, newerApplication.Id,
            Arg.Any<string>(), actor, to, AuditEntityTypes.User);
    }

    [HumansFact]
    public async Task HasAnything_GrantOrManagerOrOpenProgram()
    {
        var service = NewService();
        var nobody = SeedUser("Nobody");
        (await service.HasAnythingForUserAsync(nobody, Ct)).Should().BeFalse();

        var closed = await SeedProgramAsync(status: ProgramStatus.Closed);
        (await service.HasAnythingForUserAsync(nobody, Ct)).Should().BeFalse();
        var notAccepting = await SeedProgramAsync(acceptsApplications: false);
        (await service.HasAnythingForUserAsync(nobody, Ct)).Should().BeFalse();
        var expired = await SeedProgramAsync(claimBy: Today.PlusDays(-1));
        (await service.HasAnythingForUserAsync(nobody, Ct)).Should().BeFalse();

        var holder = SeedUser("Holder");
        await SeedGrantAsync(closed, holder, status: GrantStatus.Declined);
        (await service.HasAnythingForUserAsync(holder, Ct)).Should().BeTrue();
        var manager = SeedUser("Manager");
        await SeedProgramAsync(status: ProgramStatus.Closed, managers: manager);
        (await service.HasAnythingForUserAsync(manager, Ct)).Should().BeTrue();
        (await service.ManagesAnyProgramAsync(manager, Ct)).Should().BeTrue();
        (await service.ManagesAnyProgramAsync(holder, Ct)).Should().BeFalse();

        await SeedProgramAsync();
        (await service.HasAnythingForUserAsync(nobody, Ct)).Should().BeTrue();
        _ = (notAccepting, expired);
    }
}
