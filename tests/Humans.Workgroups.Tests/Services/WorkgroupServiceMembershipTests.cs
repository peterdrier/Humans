using Xunit;
using AwesomeAssertions;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Services;
using Humans.Workgroups.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Workgroups.Tests.Services;

/// <summary>
/// Coordinators (1-2, current members only, the last one needs a replacement) and the
/// seven-day status-request cooldown (design §5, §7, §13, §20).
/// </summary>
public sealed class WorkgroupServiceMembershipTests : WorkgroupsTestHarness
{
    // ── Coordinator count and membership ─────────────────────────────────

    [HumansTheory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task SetCoordinators_OutsideOneOrTwo_Throws(int count)
    {
        var workgroup = await SeedWorkgroupAsync();
        var coordinator = workgroup.Members.Single().UserId;
        var others = Enumerable.Range(0, 3).Select(_ => SeedUser()).ToList();
        foreach (var id in others)
            await AddMemberAsync(workgroup.Id, id);

        var wanted = count switch
        {
            0 => (IReadOnlyList<Guid>)[],
            _ => [coordinator, .. others]
        };

        var act = () => NewService().SetCoordinatorsAsync(workgroup.Id, coordinator, wanted, ct: Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.CoordinatorCount);
    }

    [HumansFact]
    public async Task SetCoordinators_ANonMember_Throws()
    {
        var workgroup = await SeedWorkgroupAsync();
        var coordinator = workgroup.Members.Single().UserId;
        var stranger = SeedUser("Stranger");

        var act = () => NewService().SetCoordinatorsAsync(
            workgroup.Id, coordinator, [coordinator, stranger], ct: Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.CoordinatorsMustBeMembers);
    }

    [HumansFact]
    public async Task SetCoordinators_TwoCurrentMembers_Succeeds()
    {
        var workgroup = await SeedWorkgroupAsync();
        var coordinator = workgroup.Members.Single().UserId;
        var second = SeedUser("Second");
        await AddMemberAsync(workgroup.Id, second);

        await NewService().SetCoordinatorsAsync(workgroup.Id, coordinator, [coordinator, second], ct: Ct);

        await using var ctx = OpenContext();
        var members = await ctx.Members.Where(m => m.WorkgroupId == workgroup.Id).ToListAsync(Ct);
        members.Where(m => m.Role == WorkgroupMemberRole.Coordinator).Select(m => m.UserId)
            .Should().BeEquivalentTo([coordinator, second]);
    }

    // ── Leaving as the last coordinator ──────────────────────────────────

    [HumansFact]
    public async Task Leave_AsTheLastCoordinator_WithoutReplacement_Throws()
    {
        var workgroup = await SeedWorkgroupAsync();
        var coordinator = workgroup.Members.Single().UserId;

        var act = () => NewService().LeaveAsync(workgroup.Id, coordinator, null, ct: Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.LastCoordinatorNeedsReplacement);
    }

    [HumansFact]
    public async Task Leave_AsTheLastCoordinator_NamingAReplacement_PromotesThem()
    {
        var workgroup = await SeedWorkgroupAsync();
        var coordinator = workgroup.Members.Single().UserId;
        var replacement = SeedUser("Replacement");
        await AddMemberAsync(workgroup.Id, replacement);

        await NewService().LeaveAsync(workgroup.Id, coordinator, replacement, ct: Ct);

        await using var ctx = OpenContext();
        var members = await ctx.Members.Where(m => m.WorkgroupId == workgroup.Id).ToListAsync(Ct);
        members.Single(m => m.UserId == coordinator).LeftAt.Should().NotBeNull();
        members.Single(m => m.UserId == replacement).Role.Should().Be(WorkgroupMemberRole.Coordinator);
    }

    [HumansFact]
    public async Task Leave_AsTheLastCoordinator_AsAdmin_OverridesAndLeavesItCoordinatorless()
    {
        var workgroup = await SeedWorkgroupAsync();
        var coordinator = workgroup.Members.Single().UserId;

        await NewService().LeaveAsync(workgroup.Id, coordinator, null, asAdmin: true, ct: Ct);

        await using var ctx = OpenContext();
        var members = await ctx.Members.Where(m => m.WorkgroupId == workgroup.Id).ToListAsync(Ct);
        members.Single().LeftAt.Should().NotBeNull();
    }

    [HumansFact]
    public async Task Leave_NotACoordinator_DoesNotRequireAReplacement()
    {
        var workgroup = await SeedWorkgroupAsync();
        var member = SeedUser("Member");
        await AddMemberAsync(workgroup.Id, member);

        await NewService().LeaveAsync(workgroup.Id, member, null, ct: Ct);

        await using var ctx = OpenContext();
        (await ctx.Members.SingleAsync(m => m.UserId == member, Ct)).LeftAt.Should().NotBeNull();
    }

    [HumansFact]
    public async Task Join_OnADormantGroup_Throws()
    {
        var workgroup = await SeedWorkgroupAsync(
            status: WorkgroupStatus.Dormant, dormantReason: WorkgroupDormantReason.Quiet);

        var act = () => NewService().JoinAsync(workgroup.Id, SeedUser(), Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.Frozen);
    }

    [HumansFact]
    public async Task Join_Twice_Throws()
    {
        var workgroup = await SeedWorkgroupAsync();
        var member = SeedUser();
        await NewService().JoinAsync(workgroup.Id, member, Ct);

        var act = () => NewService().JoinAsync(workgroup.Id, member, Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.AlreadyAMember);
    }

    // ── Status request cooldown (§13, §20) ───────────────────────────────

    [HumansFact]
    public async Task RequestStatus_TwiceWithinSevenDays_SecondThrows()
    {
        var workgroup = await SeedWorkgroupAsync();
        var requester = SeedUser("Curious");
        await NewService().RequestStatusAsync(workgroup.Id, requester, "How's it going?", Ct);

        var act = () => NewService().RequestStatusAsync(workgroup.Id, requester, "Again?", Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.StatusRequestOnCooldown);
    }

    [HumansFact]
    public async Task RequestStatus_AfterSevenDays_Succeeds()
    {
        var workgroup = await SeedWorkgroupAsync();
        var requester = SeedUser("Curious");
        await NewService().RequestStatusAsync(workgroup.Id, requester, "How's it going?", Ct);

        Clock.Advance(Duration.FromDays(7));
        await NewService().RequestStatusAsync(workgroup.Id, requester, "Again?", Ct);

        await using var ctx = OpenContext();
        (await ctx.LogEntries.CountAsync(
                e => e.WorkgroupId == workgroup.Id && e.Kind == WorkgroupLogKind.StatusRequested, Ct))
            .Should().Be(2);
    }

    [HumansFact]
    public async Task RequestStatus_TwoDifferentPeople_BothWithinSevenDays_BothSucceed()
    {
        var workgroup = await SeedWorkgroupAsync();
        var first = SeedUser("First");
        var second = SeedUser("Second");
        await NewService().RequestStatusAsync(workgroup.Id, first, null, Ct);

        await NewService().RequestStatusAsync(workgroup.Id, second, null, Ct);

        await using var ctx = OpenContext();
        (await ctx.LogEntries.CountAsync(
                e => e.WorkgroupId == workgroup.Id && e.Kind == WorkgroupLogKind.StatusRequested, Ct))
            .Should().Be(2);
    }
}
