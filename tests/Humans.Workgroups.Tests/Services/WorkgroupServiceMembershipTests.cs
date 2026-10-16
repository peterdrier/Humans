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
        entries.Should().HaveCount(string.Equals(action, "handover", StringComparison.Ordinal) ? 2 : 1);
        await repository.DidNotReceive().AddLogEntryAsync(Arg.Any<WorkgroupLogEntry>(), cancellation.Token);
        if (string.Equals(action, "coordinators", StringComparison.Ordinal))
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
    [InlineData("join")]
    [InlineData("leave")]
    [InlineData("handover")]
    [InlineData("coordinators")]
    public async Task Membership_NamePreparationFailure_LeavesMembersAndLogUnchanged(string action)
    {
        var group = await SeedWorkgroupAsync();
        var coordinator = group.Members.Single().UserId;
        var member = SeedUser("Member");
        if (!string.Equals(action, "join", StringComparison.Ordinal))
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
            "join" => () => service.JoinAsync(group.Id, member, Ct),
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
            .Should().Be(WorkgroupErrorKeys.AlreadyAMember);
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

        // The tombstoned row is still readable, so a double-submitted form used to re-stamp it and
        // audit a second deletion — an event the trail would have claimed happened, and did not.
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
}
