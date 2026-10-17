using Xunit;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Services;
using Humans.Workgroups.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NodaTime;
using Humans.Users.Contracts;
using Humans.Workgroups.Data;

namespace Humans.Workgroups.Tests.Services;

/// <summary>
/// Coordinators (1-2, current members only, the last one needs a replacement), status
/// requests, and the audit trail behind edits that overwrite a row (design §5, §7, §20).
/// </summary>
public sealed class WorkgroupServiceMembershipTests : WorkgroupsTestHarness
{
    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisterEditOrStatusRequest_FinishesAfterCommittedWrite(bool statusRequest)
    {
        var group = await SeedWorkgroupAsync();
        var actor = group.Members.Single().UserId;
        var people = await Users.GetUserInfosAsync([actor], Ct);
        Users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                c.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(people);
            });
        using var cancellation = new CancellationTokenSource();
        var real = new WorkgroupRepository(DbFactory);
        var repository = Substitute.For<IWorkgroupRepository>();
        repository.GetWorkgroupAsync(group.Id, Arg.Any<CancellationToken>())
            .Returns(c => real.GetWorkgroupAsync(group.Id, c.Arg<CancellationToken>()));
        repository.UpdateWorkgroupAsync(Arg.Any<Workgroup>(), Arg.Any<CancellationToken>())
            .Returns(async c =>
            {
                c.Arg<CancellationToken>().Should().Be(cancellation.Token);
                await real.UpdateWorkgroupAsync(c.Arg<Workgroup>(), c.Arg<CancellationToken>());
                await cancellation.CancelAsync();
            });
        repository.AddLogEntryAsync(Arg.Any<WorkgroupLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(async c =>
            {
                c.Arg<CancellationToken>().Should().Be(statusRequest ? cancellation.Token : CancellationToken.None);
                await real.AddLogEntryAsync(c.Arg<WorkgroupLogEntry>(), c.Arg<CancellationToken>());
                if (statusRequest) await cancellation.CancelAsync();
            });
        var service = NewService(repository);
        if (statusRequest) await service.RequestStatusAsync(group.Id, actor, "Progress?", cancellation.Token);
        else await service.EditRegisterAsync(group.Id, actor,
            new WorkgroupRegisterEdit(group.Name, group.Purpose, "Changed deliverable",
                group.DeliverableKind, group.Audience, group.TargetDate, group.DiscordChannelUrl), cancellation.Token);

        await using var db = OpenContext();
        (await db.LogEntries.SingleAsync(e => e.WorkgroupId == group.Id, Ct)).Kind
            .Should().Be(statusRequest ? WorkgroupLogKind.StatusRequested : WorkgroupLogKind.ScopeChanged);
        if (statusRequest)
            Notifications.ReceivedCalls().Should().ContainSingle().Which.GetArguments()
                .OfType<CancellationToken>().Should().ContainSingle().Which.Should().Be(CancellationToken.None);
        else (await db.Workgroups.FindAsync([group.Id], Ct))!.Deliverable.Should().Be("Changed deliverable");
    }

    [HumansTheory]
    [InlineData("join")]
    [InlineData("leave")]
    [InlineData("handover")]
    [InlineData("coordinators")]
    public async Task Membership_CommittedWriteFinishesAfterRequestCancellation(string action)
    {
        var group = await SeedWorkgroupAsync();
        var coordinator = group.Members.Single().UserId;
        var member = SeedUser("Member");
        if (!string.Equals(action, "join", StringComparison.Ordinal)) await AddMemberAsync(group.Id, member);
        using var cancellation = new CancellationTokenSource();
        var real = new WorkgroupRepository(DbFactory);
        var repository = Substitute.For<IWorkgroupRepository>();
        repository.GetWorkgroupAsync(group.Id, Arg.Any<CancellationToken>())
            .Returns(c => real.GetWorkgroupAsync(group.Id, c.Arg<CancellationToken>()));
        repository.AddMemberAsync(Arg.Any<WorkgroupMember>(), Arg.Any<CancellationToken>())
            .Returns(async c =>
            {
                c.Arg<CancellationToken>().Should().Be(cancellation.Token);
                await real.AddMemberAsync(c.Arg<WorkgroupMember>(), c.Arg<CancellationToken>());
                await cancellation.CancelAsync();
            });
        repository.UpdateMembersAsync(Arg.Any<IReadOnlyList<WorkgroupMember>>(), Arg.Any<CancellationToken>())
            .Returns(async c =>
            {
                c.Arg<CancellationToken>().Should().Be(cancellation.Token);
                await real.UpdateMembersAsync(c.Arg<IReadOnlyList<WorkgroupMember>>(), c.Arg<CancellationToken>());
                await cancellation.CancelAsync();
            });
        repository.AddLogEntryAsync(Arg.Any<WorkgroupLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(c => real.AddLogEntryAsync(c.Arg<WorkgroupLogEntry>(), c.Arg<CancellationToken>()));
        var service = NewService(repository);
        Func<Task> mutate = action switch
        {
            "join" => () => service.JoinAsync(group.Id, member, cancellation.Token),
            "leave" => () => service.LeaveAsync(group.Id, member, null, ct: cancellation.Token),
            "handover" => () => service.LeaveAsync(group.Id, coordinator, member, ct: cancellation.Token),
            _ => () => service.SetCoordinatorsAsync(group.Id, coordinator, [member], ct: cancellation.Token)
        };

        await mutate.Should().NotThrowAsync();

        await using var db = OpenContext();
        var entries = await db.LogEntries.Where(e => e.WorkgroupId == group.Id).ToListAsync(Ct);
        entries.Should().HaveCount(action switch { "handover" => 2, "join" => 0, _ => 1 });
        await repository.DidNotReceive().AddLogEntryAsync(Arg.Any<WorkgroupLogEntry>(), cancellation.Token);
        if (string.Equals(action, "join", StringComparison.Ordinal))
        {
            // A request is not membership yet: the coordinators are told, nothing else moves.
            Notifications.ReceivedCalls().Should().ContainSingle().Which.GetArguments()
                .OfType<CancellationToken>().Should().ContainSingle().Which.Should().Be(CancellationToken.None);
            await GoogleSync.DidNotReceive().RequestSyncAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        else if (string.Equals(action, "coordinators", StringComparison.Ordinal))
        {
            AuditLog.ReceivedCalls().Should().ContainSingle();
            Notifications.ReceivedCalls().Should().ContainSingle().Which.GetArguments()
                .OfType<CancellationToken>().Should().ContainSingle().Which.Should().Be(CancellationToken.None);
            Email.ReceivedCalls().Should().HaveCount(2);
            Email.ReceivedCalls().SelectMany(c => c.GetArguments().OfType<CancellationToken>())
                .Should().OnlyContain(ct => ct == CancellationToken.None);
        }
        else
            await GoogleSync.Received(1).RequestSyncAsync(group.DriveFolderId!, CancellationToken.None);
        if (string.Equals(action, "handover", StringComparison.Ordinal))
            AuditLog.ReceivedCalls().Should().ContainSingle();
    }

    [HumansTheory]
    [InlineData("leave")]
    [InlineData("handover")]
    [InlineData("coordinators")]
    public async Task Membership_NamePreparationFailure_LeavesMembersAndLogUnchanged(string action)
    {
        var group = await SeedWorkgroupAsync();
        var coordinator = group.Members.Single().UserId;
        var member = SeedUser("Member");
        await AddMemberAsync(group.Id, member);
        var failure = new IOException("required name lookup failed");
        Users.GetUserInfoAsync(member, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<UserInfo?>(failure));
        Users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<IReadOnlyDictionary<Guid, UserInfo>>(failure));
        await using var beforeContext = OpenContext();
        var before = await beforeContext.Members.Where(m => m.WorkgroupId == group.Id)
            .AsNoTracking().ToListAsync(Ct);
        var service = NewService();
        Func<Task> mutate = action switch
        {
            "leave" => () => service.LeaveAsync(group.Id, member, null, ct: Ct),
            "handover" => () => service.LeaveAsync(group.Id, coordinator, member, ct: Ct),
            _ => () => service.SetCoordinatorsAsync(group.Id, coordinator, [member], ct: Ct)
        };

        (await mutate.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);

        await using var afterContext = OpenContext();
        var after = await afterContext.Members.Where(m => m.WorkgroupId == group.Id)
            .AsNoTracking().ToListAsync(Ct);
        after.Select(m => (m.Id, m.UserId, m.Role, m.LeftAt))
            .Should().BeEquivalentTo(before.Select(m => (m.Id, m.UserId, m.Role, m.LeftAt)));
        (await afterContext.LogEntries.AnyAsync(e => e.WorkgroupId == group.Id, Ct)).Should().BeFalse();
    }

    // ── Coordinator count and membership ─────────────────────────────────

    [HumansTheory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task SetCoordinators_OutsideOneOrTwo_ReturnsARefusal(int count)
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

        (await act()).Refusal!.Key
            .Should().Be(WorkgroupErrorKeys.CoordinatorCount);
    }

    [HumansFact]
    public async Task SetCoordinators_ANonMember_ReturnsARefusal()
    {
        var workgroup = await SeedWorkgroupAsync();
        var coordinator = workgroup.Members.Single().UserId;
        var stranger = SeedUser("Stranger");

        var act = () => NewService().SetCoordinatorsAsync(
            workgroup.Id, coordinator, [coordinator, stranger], ct: Ct);

        (await act()).Refusal!.Key
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
    public async Task Leave_AsTheLastCoordinator_WithoutReplacement_ReturnsARefusal()
    {
        var workgroup = await SeedWorkgroupAsync();
        var coordinator = workgroup.Members.Single().UserId;

        var act = () => NewService().LeaveAsync(workgroup.Id, coordinator, null, ct: Ct);

        (await act()).Refusal!.Key
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
    public async Task Join_OnADormantGroup_ReturnsARefusal()
    {
        var workgroup = await SeedWorkgroupAsync(
            status: WorkgroupStatus.Dormant, dormantReason: WorkgroupDormantReason.Quiet);

        var act = () => NewService().JoinAsync(workgroup.Id, SeedUser(), Ct);

        (await act()).Refusal!.Key
            .Should().Be(WorkgroupErrorKeys.Frozen);
    }

    [HumansFact]
    public async Task Join_Twice_ReturnsARefusal()
    {
        var workgroup = await SeedWorkgroupAsync();
        var member = SeedUser();
        await NewService().JoinAsync(workgroup.Id, member, Ct);

        var act = () => NewService().JoinAsync(workgroup.Id, member, Ct);

        (await act()).Refusal!.Key
            .Should().Be(WorkgroupErrorKeys.MembershipPending);
    }

    // ── Join requests: a coordinator decides ─────────────────────────────

    [HumansFact]
    public async Task Join_CreatesAPendingRow_ThatIsNotAMemberYet()
    {
        var workgroup = await SeedWorkgroupAsync();
        var requester = SeedUser("Requester");

        await NewService().JoinAsync(workgroup.Id, requester, Ct);

        var info = (await NewService().GetByIdAsync(workgroup.Id, Ct))!;
        info.IsMember(requester).Should().BeFalse();
        info.IsPendingMember(requester).Should().BeTrue();
        info.CurrentMemberUserIds().Should().NotContain(requester);
        (await OpenContext().LogEntries.AnyAsync(e => e.WorkgroupId == workgroup.Id, Ct)).Should().BeFalse();
        await Notifications.Received(1).SendAsync(
            Humans.Notifications.Contracts.NotificationSource.WorkgroupMembershipRequested,
            Arg.Any<Humans.Notifications.Contracts.NotificationClass>(),
            Arg.Any<Humans.Notifications.Contracts.NotificationPriority>(),
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Single() == workgroup.Members.Single().UserId),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApproveMember_ByACoordinatorOrAdmin_AdmitsWithLogAuditNoticeAndDriveSync(bool asAdmin)
    {
        var workgroup = await SeedWorkgroupAsync();
        var coordinator = workgroup.Members.Single().UserId;
        var requester = SeedUser("Requester");
        await AddMemberAsync(workgroup.Id, requester, isPending: true);
        var actor = asAdmin ? SeedUser("Secretary") : coordinator;

        var result = await NewService().ApproveMemberAsync(workgroup.Id, actor, requester, asAdmin, Ct);

        result.Refusal.Should().BeNull();
        var info = (await NewService().GetByIdAsync(workgroup.Id, Ct))!;
        info.IsMember(requester).Should().BeTrue();
        info.PendingMembers().Should().BeEmpty();
        (await OpenContext().LogEntries.SingleAsync(e => e.WorkgroupId == workgroup.Id, Ct)).Kind
            .Should().Be(WorkgroupLogKind.MemberJoined);
        await AuditLog.Received(1).LogAsync(AuditAction.WorkgroupMembershipApproved, Arg.Any<string>(), workgroup.Id,
            Arg.Any<string>(), actor, requester, "User");
        await Notifications.Received(1).SendAsync(
            Humans.Notifications.Contracts.NotificationSource.WorkgroupMembershipDecided,
            Arg.Any<Humans.Notifications.Contracts.NotificationClass>(),
            Arg.Any<Humans.Notifications.Contracts.NotificationPriority>(),
            Arg.Any<string>(), Arg.Is<IReadOnlyList<Guid>>(ids => ids.Single() == requester),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await GoogleSync.Received(1).RequestSyncAsync(workgroup.DriveFolderId!, CancellationToken.None);
    }

    [HumansTheory]
    [InlineData("approve")]
    [InlineData("decline")]
    [InlineData("add")]
    public async Task MembershipDecisions_ByAPlainMember_AreRefused(string action)
    {
        var workgroup = await SeedWorkgroupAsync();
        var member = SeedUser("Member");
        await AddMemberAsync(workgroup.Id, member);
        var subject = SeedUser("Subject");
        await AddMemberAsync(workgroup.Id, subject, isPending: true);
        var service = NewService();

        var result = action switch
        {
            "approve" => await service.ApproveMemberAsync(workgroup.Id, member, subject, ct: Ct),
            "decline" => await service.DeclineMemberAsync(workgroup.Id, member, subject, ct: Ct),
            _ => await service.AddMemberAsync(workgroup.Id, member, SeedUser("Newcomer"), ct: Ct)
        };

        result.Refusal!.Key.Should().Be(WorkgroupErrorKeys.NotACoordinator);
        (await OpenContext().Members.SingleAsync(m => m.UserId == subject, Ct)).IsPending.Should().BeTrue();
        AuditLog.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task DeclineMember_ClosesTheRequest_AuditedAndTold()
    {
        var workgroup = await SeedWorkgroupAsync();
        var coordinator = workgroup.Members.Single().UserId;
        var requester = SeedUser("Requester");
        await AddMemberAsync(workgroup.Id, requester, isPending: true);

        var result = await NewService().DeclineMemberAsync(workgroup.Id, coordinator, requester, ct: Ct);

        result.Refusal.Should().BeNull();
        var row = await OpenContext().Members.SingleAsync(m => m.UserId == requester, Ct);
        row.LeftAt.Should().NotBeNull();
        (await NewService().GetByIdAsync(workgroup.Id, Ct))!.PendingMembers().Should().BeEmpty();
        (await OpenContext().LogEntries.AnyAsync(e => e.WorkgroupId == workgroup.Id, Ct)).Should().BeFalse();
        await AuditLog.Received(1).LogAsync(AuditAction.WorkgroupMembershipDeclined, Arg.Any<string>(), workgroup.Id,
            Arg.Any<string>(), coordinator, requester, "User");
        await Notifications.Received(1).SendAsync(
            Humans.Notifications.Contracts.NotificationSource.WorkgroupMembershipDecided,
            Arg.Any<Humans.Notifications.Contracts.NotificationClass>(),
            Arg.Any<Humans.Notifications.Contracts.NotificationPriority>(),
            Arg.Any<string>(), Arg.Is<IReadOnlyList<Guid>>(ids => ids.Single() == requester),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await GoogleSync.DidNotReceive().RequestSyncAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ApproveOrDecline_WithNoPendingRequest_ReturnsARefusal()
    {
        var workgroup = await SeedWorkgroupAsync();
        var coordinator = workgroup.Members.Single().UserId;
        var stranger = SeedUser("Stranger");

        (await NewService().ApproveMemberAsync(workgroup.Id, coordinator, stranger, ct: Ct)).Refusal!.Key
            .Should().Be(WorkgroupErrorKeys.NoPendingRequest);
        (await NewService().DeclineMemberAsync(workgroup.Id, coordinator, stranger, ct: Ct)).Refusal!.Key
            .Should().Be(WorkgroupErrorKeys.NoPendingRequest);
    }

    [HumansTheory]
    [InlineData(UserState.Deleted)]
    [InlineData(UserState.Merged)]
    public async Task AddMember_TerminalAccount_DoesNotCreateMembership(UserState state)
    {
        var group = await SeedWorkgroupAsync();
        var coordinator = group.Members.Single().UserId;
        var target = SeedUser("Terminal");
        var person = (await Users.GetUserInfoAsync(target, Ct))! with { State = state, MergedAt = Clock.GetCurrentInstant() };
        Users.GetUserInfoAsync(target, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(person));

        var result = await NewService().AddMemberAsync(group.Id, coordinator, target, ct: Ct);

        result.Refusal!.Key.Should().Be(WorkgroupErrorKeys.NotFound);
        await using var ctx = OpenContext();
        (await ctx.Members.CountAsync(m => m.UserId == target, Ct)).Should().Be(0);
        AuditLog.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task AddMember_MergeAlias_ApprovesSurvivorPendingRequest()
    {
        var group = await SeedWorkgroupAsync();
        var coordinator = group.Members.Single().UserId;
        var survivor = SeedUser("Survivor");
        await AddMemberAsync(group.Id, survivor, isPending: true);
        var source = Guid.NewGuid();
        var survivorInfo = await Users.GetUserInfoAsync(survivor, Ct);
        Users.GetUserInfoAsync(source, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(survivorInfo));

        (await NewService().AddMemberAsync(group.Id, coordinator, source, ct: Ct)).Refusal.Should().BeNull();

        await using var ctx = OpenContext();
        var member = await ctx.Members.SingleAsync(m => m.UserId == survivor, Ct);
        member.IsPending.Should().NotBe(true);
        (await ctx.Members.CountAsync(m => m.UserId == source, Ct)).Should().Be(0);
        await AuditLog.Received(1).LogAsync(AuditAction.WorkgroupMemberAdded, Arg.Any<string>(), group.Id,
            Arg.Any<string>(), coordinator, survivor, "User");
    }

    [HumansFact]
    public async Task AddMember_ByACoordinator_AdmitsDirectly()
    {
        var workgroup = await SeedWorkgroupAsync();
        var coordinator = workgroup.Members.Single().UserId;
        var newcomer = SeedUser("Newcomer");

        var result = await NewService().AddMemberAsync(workgroup.Id, coordinator, newcomer, ct: Ct);

        result.Refusal.Should().BeNull();
        var info = (await NewService().GetByIdAsync(workgroup.Id, Ct))!;
        info.IsMember(newcomer).Should().BeTrue();
        (await OpenContext().LogEntries.SingleAsync(e => e.WorkgroupId == workgroup.Id, Ct)).Kind
            .Should().Be(WorkgroupLogKind.MemberJoined);
        await AuditLog.Received(1).LogAsync(AuditAction.WorkgroupMemberAdded, Arg.Any<string>(), workgroup.Id,
            Arg.Any<string>(), coordinator, newcomer, "User");
        await GoogleSync.Received(1).RequestSyncAsync(workgroup.DriveFolderId!, CancellationToken.None);
    }

    [HumansFact]
    public async Task AddMember_WithAPendingRequest_ApprovesIt()
    {
        var workgroup = await SeedWorkgroupAsync();
        var coordinator = workgroup.Members.Single().UserId;
        var requester = SeedUser("Requester");
        await AddMemberAsync(workgroup.Id, requester, isPending: true);

        var result = await NewService().AddMemberAsync(workgroup.Id, coordinator, requester, ct: Ct);

        result.Refusal.Should().BeNull();
        (await OpenContext().Members.Where(m => m.UserId == requester).ToListAsync(Ct))
            .Should().ContainSingle().Which.IsPending.Should().NotBe(true);
    }

    [HumansFact]
    public async Task AddMember_AnExistingMemberOrUnknownUser_ReturnsARefusal()
    {
        var workgroup = await SeedWorkgroupAsync();
        var coordinator = workgroup.Members.Single().UserId;

        (await NewService().AddMemberAsync(workgroup.Id, coordinator, coordinator, ct: Ct)).Refusal!.Key
            .Should().Be(WorkgroupErrorKeys.AlreadyAMember);
        (await NewService().AddMemberAsync(workgroup.Id, coordinator, Guid.NewGuid(), ct: Ct)).Refusal!.Key
            .Should().Be(WorkgroupErrorKeys.NotFound);
    }

    [HumansFact]
    public async Task Leave_WhilePending_WithdrawsTheRequestWithoutALogEntry()
    {
        var workgroup = await SeedWorkgroupAsync();
        var requester = SeedUser("Requester");
        await AddMemberAsync(workgroup.Id, requester, isPending: true);

        var result = await NewService().LeaveAsync(workgroup.Id, requester, null, ct: Ct);

        result.Refusal.Should().BeNull();
        (await OpenContext().Members.SingleAsync(m => m.UserId == requester, Ct)).LeftAt.Should().NotBeNull();
        (await OpenContext().LogEntries.AnyAsync(e => e.WorkgroupId == workgroup.Id, Ct)).Should().BeFalse();
        await GoogleSync.DidNotReceive().RequestSyncAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Leave_NamingAPendingRequesterAsReplacement_ReturnsARefusal()
    {
        var workgroup = await SeedWorkgroupAsync();
        var coordinator = workgroup.Members.Single().UserId;
        var requester = SeedUser("Requester");
        await AddMemberAsync(workgroup.Id, requester, isPending: true);

        var result = await NewService().LeaveAsync(workgroup.Id, coordinator, requester, ct: Ct);

        result.Refusal!.Key.Should().Be(WorkgroupErrorKeys.CoordinatorsMustBeMembers);
        (await OpenContext().Members.SingleAsync(m => m.UserId == coordinator, Ct)).LeftAt.Should().BeNull();
    }

    [HumansTheory]
    [InlineData("close")]
    [InlineData("withdraw")]
    [InlineData("done")]
    public async Task EndingOrWithdrawingTheGroup_ClosesItsPendingRequests(string action)
    {
        var workgroup = await SeedWorkgroupAsync();
        var coordinator = workgroup.Members.Single().UserId;
        var requester = SeedUser("Requester");
        await AddMemberAsync(workgroup.Id, requester, isPending: true);
        var service = NewService();

        var result = action switch
        {
            "close" => await service.CloseAsync(workgroup.Id, SeedUser("Secretary"), "Quiet", Ct),
            "withdraw" => await service.WithdrawAsync(workgroup.Id, SeedUser("Secretary"), "Withdrawn", Ct),
            _ => await service.MarkDoneAsync(workgroup.Id, coordinator, WorkgroupDormantReason.Delivered, Ct)
        };

        result.Refusal.Should().BeNull();
        (await OpenContext().Members.SingleAsync(m => m.UserId == requester, Ct)).LeftAt.Should().NotBeNull();
        (await service.GetByIdAsync(workgroup.Id, Ct))!.PendingMembers().Should().BeEmpty();
    }

    [HumansFact]
    public async Task SetCoordinators_APendingMember_ReturnsARefusal()
    {
        var workgroup = await SeedWorkgroupAsync();
        var coordinator = workgroup.Members.Single().UserId;
        var requester = SeedUser("Requester");
        await AddMemberAsync(workgroup.Id, requester, isPending: true);

        var act = () => NewService().SetCoordinatorsAsync(workgroup.Id, coordinator, [requester], ct: Ct);

        (await act()).Refusal!.Key.Should().Be(WorkgroupErrorKeys.CoordinatorsMustBeMembers);
    }

    // ── Status requests ──────────────────────────────────────────────────

    [HumansFact]
    public async Task RequestStatus_TwiceInARow_BothLand()
    {
        var workgroup = await SeedWorkgroupAsync();
        var requester = SeedUser("Curious");
        await NewService().RequestStatusAsync(workgroup.Id, requester, "How's it going?", Ct);

        await NewService().RequestStatusAsync(workgroup.Id, requester, "Again?", Ct);

        // Asking is not rate-limited: the section does not track who asked when.
        await using var ctx = OpenContext();
        (await ctx.LogEntries.CountAsync(
                e => e.WorkgroupId == workgroup.Id && e.Kind == WorkgroupLogKind.StatusRequested, Ct))
            .Should().Be(2);
    }

    [HumansFact]
    public async Task RequestStatus_TwoDifferentPeople_BothSucceed()
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

    // ── Linking a survey ─────────────────────────────────────────────────

    [HumansFact]
    public async Task LinkSurvey_ASurveyTheMemberAuthored_WritesTheEntry()
    {
        var workgroup = await SeedWorkgroupAsync();
        var author = SeedUser("Author");
        var surveyId = SeedSurvey(author);

        await NewService().LinkSurveyAsync(workgroup.Id, author, surveyId, Ct);

        await using var ctx = OpenContext();
        var entry = await ctx.LogEntries.SingleAsync(
            e => e.WorkgroupId == workgroup.Id && e.Kind == WorkgroupLogKind.SurveySubmitted, Ct);
        entry.SurveyId.Should().Be(surveyId);
    }

    [HumansFact]
    public async Task LinkSurvey_SomebodyElsesSurvey_ReturnsARefusal()
    {
        var workgroup = await SeedWorkgroupAsync();
        var author = SeedUser("Author");
        var member = SeedUser("Member");
        var surveyId = SeedSurvey(author);

        var act = () => NewService().LinkSurveyAsync(workgroup.Id, member, surveyId, Ct);

        (await act()).Refusal!.Key.Should().Be(WorkgroupErrorKeys.SurveyNotYours);
    }

    [HumansFact]
    public async Task LinkSurvey_ASurveyIdThatDoesNotExist_ReturnsARefusal()
    {
        var workgroup = await SeedWorkgroupAsync();
        var member = SeedUser("Member");

        var act = () => NewService().LinkSurveyAsync(workgroup.Id, member, Guid.NewGuid(), Ct);

        (await act()).Refusal!.Key.Should().Be(WorkgroupErrorKeys.SurveyNotYours);
    }

    // ── Edits that overwrite a row leave an audit entry ──────────────────

    [HumansFact]
    public async Task EditingALogEntry_IsAudited()
    {
        var workgroup = await SeedWorkgroupAsync();
        var member = workgroup.Members.Single().UserId;
        var today = Clock.GetCurrentInstant().InUtc().Date;
        var entryId = (await NewService().AddLogEntryAsync(
            workgroup.Id, member,
            new WorkgroupLogEntrySave(WorkgroupLogKind.Note, today, null, "First wording"), Ct)).Value;

        await NewService().UpdateLogEntryAsync(
            entryId, member,
            new WorkgroupLogEntrySave(WorkgroupLogKind.Note, today, null, "Second wording"), Ct);

        // The row is overwritten in place, so the audit entry is the only trace that the
        // members read something else before.
        await AuditLog.Received(1).LogAsync(
            AuditAction.WorkgroupLogEntryUpdated, AuditEntityTypes.WorkgroupLogEntry, entryId,
            Arg.Any<string>(), member, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task EditingAMeeting_IsAudited()
    {
        var workgroup = await SeedWorkgroupAsync();
        var member = workgroup.Members.Single().UserId;
        var now = Clock.GetCurrentInstant();
        var meetingId = (await NewService().CreateMeetingAsync(
            workgroup.Id, member,
            new WorkgroupMeetingSave("Sync", now, now.Plus(Duration.FromHours(1)), null, null, false, null), Ct)).Value;

        await NewService().UpdateMeetingAsync(
            meetingId, member,
            new WorkgroupMeetingSave("Sync, moved", now.Plus(Duration.FromDays(1)),
                now.Plus(Duration.FromDays(1)).Plus(Duration.FromHours(1)), null, null, false, null), Ct);

        await AuditLog.Received(1).LogAsync(
            AuditAction.WorkgroupMeetingUpdated, AuditEntityTypes.WorkgroupMeeting, meetingId,
            Arg.Any<string>(), member, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task DeletingAMeeting_IsAudited()
    {
        var workgroup = await SeedWorkgroupAsync();
        var member = workgroup.Members.Single().UserId;
        var now = Clock.GetCurrentInstant();
        var meetingId = (await NewService().CreateMeetingAsync(
            workgroup.Id, member,
            new WorkgroupMeetingSave("Sync", now, now.Plus(Duration.FromHours(1)), null, null, false, null), Ct)).Value;

        await NewService().DeleteMeetingAsync(meetingId, member, Ct);

        await AuditLog.Received(1).LogAsync(
            AuditAction.WorkgroupMeetingDeleted, AuditEntityTypes.WorkgroupMeeting, meetingId,
            Arg.Any<string>(), member, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task ReplayingADelete_IsRefused_AndAuditedOnce()
    {
        var workgroup = await SeedWorkgroupAsync();
        var member = workgroup.Members.Single().UserId;
        var now = Clock.GetCurrentInstant();
        var meetingId = (await NewService().CreateMeetingAsync(
            workgroup.Id, member,
            new WorkgroupMeetingSave("Sync", now, now.Plus(Duration.FromHours(1)), null, null, false, null), Ct)).Value;
        await NewService().DeleteMeetingAsync(meetingId, member, Ct);

        // The tombstoned row is still readable, so a replayed delete is refused rather than
        // re-stamped and audited a second time.
        var replay = async () => await NewService().DeleteMeetingAsync(meetingId, member, Ct);

        (await replay()).Refusal!.Key
            .Should().Be(WorkgroupErrorKeys.NotFound);
        await AuditLog.Received(1).LogAsync(
            AuditAction.WorkgroupMeetingDeleted, AuditEntityTypes.WorkgroupMeeting, meetingId,
            Arg.Any<string>(), member, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task EditingADeletedMeeting_IsRefused()
    {
        var workgroup = await SeedWorkgroupAsync();
        var member = workgroup.Members.Single().UserId;
        var now = Clock.GetCurrentInstant();
        var meetingId = (await NewService().CreateMeetingAsync(
            workgroup.Id, member,
            new WorkgroupMeetingSave("Sync", now, now.Plus(Duration.FromHours(1)), null, null, false, null), Ct)).Value;
        await NewService().DeleteMeetingAsync(meetingId, member, Ct);

        // Same unfiltered read, same class of bug: a deleted meeting must not be editable either.
        var edit = async () => await NewService().UpdateMeetingAsync(
            meetingId, member,
            new WorkgroupMeetingSave("Back from the dead", now, now.Plus(Duration.FromHours(1)),
                null, null, false, null), Ct);

        (await edit()).Refusal!.Key
            .Should().Be(WorkgroupErrorKeys.NotFound);
        await AuditLog.DidNotReceive().LogAsync(
            AuditAction.WorkgroupMeetingUpdated, AuditEntityTypes.WorkgroupMeeting, meetingId,
            Arg.Any<string>(), member, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansTheory]
    [InlineData(nameof(WorkgroupLogKind.Registered))]
    [InlineData(nameof(WorkgroupLogKind.MemberJoined))]
    public async Task SystemLogEntries_CannotBeEditedOrDeleted(string kindName)
    {
        var kind = Enum.Parse<WorkgroupLogKind>(kindName);
        var workgroup = await SeedWorkgroupAsync();
        var member = workgroup.Members.Single().UserId;
        var entry = await AddLogEntryAsync(workgroup.Id, kind);

        var edit = await NewService().UpdateLogEntryAsync(
            entry.Id, member,
            new WorkgroupLogEntrySave(WorkgroupLogKind.Note, entry.OccurredOn, "Rewritten", "Rewritten body"), Ct);
        var delete = await NewService().DeleteLogEntryAsync(entry.Id, member, Ct);

        edit.Refusal!.Key.Should().Be(WorkgroupErrorKeys.SystemLogEntry);
        delete.Refusal!.Key.Should().Be(WorkgroupErrorKeys.SystemLogEntry);
        await using var ctx = OpenContext();
        var stored = await ctx.LogEntries.SingleAsync(e => e.Id == entry.Id, Ct);
        stored.Kind.Should().Be(kind);
        stored.Title.Should().BeNull();
        stored.Body.Should().BeNull();
    }
}
