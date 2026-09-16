using Xunit;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Services;
using Humans.Workgroups.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NodaTime;

namespace Humans.Workgroups.Tests.Services;

/// <summary>
/// Coordinators (1-2, current members only, the last one needs a replacement), status
/// requests, and the audit trail behind edits that overwrite a row (design §5, §7, §20).
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
    public async Task LinkSurvey_SomebodyElsesSurvey_Throws()
    {
        var workgroup = await SeedWorkgroupAsync();
        var author = SeedUser("Author");
        var member = SeedUser("Member");
        var surveyId = SeedSurvey(author);

        var act = () => NewService().LinkSurveyAsync(workgroup.Id, member, surveyId, Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>())
            .Which.Key.Should().Be(WorkgroupErrorKeys.SurveyNotYours);
    }

    [HumansFact]
    public async Task LinkSurvey_ASurveyIdThatDoesNotExist_Throws()
    {
        var workgroup = await SeedWorkgroupAsync();
        var member = SeedUser("Member");

        var act = () => NewService().LinkSurveyAsync(workgroup.Id, member, Guid.NewGuid(), Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>())
            .Which.Key.Should().Be(WorkgroupErrorKeys.SurveyNotYours);
    }

    // ── Edits that overwrite a row leave an audit entry ──────────────────

    [HumansFact]
    public async Task EditingALogEntry_IsAudited()
    {
        var workgroup = await SeedWorkgroupAsync();
        var member = workgroup.Members.Single().UserId;
        var today = Clock.GetCurrentInstant().InUtc().Date;
        var entryId = await NewService().AddLogEntryAsync(
            workgroup.Id, member,
            new WorkgroupLogEntrySave(WorkgroupLogKind.Note, today, null, "First wording"), Ct);

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
        var meetingId = await NewService().CreateMeetingAsync(
            workgroup.Id, member,
            new WorkgroupMeetingSave("Sync", now, now.Plus(Duration.FromHours(1)), null, null, false, null), Ct);

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
        var meetingId = await NewService().CreateMeetingAsync(
            workgroup.Id, member,
            new WorkgroupMeetingSave("Sync", now, now.Plus(Duration.FromHours(1)), null, null, false, null), Ct);

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
        var meetingId = await NewService().CreateMeetingAsync(
            workgroup.Id, member,
            new WorkgroupMeetingSave("Sync", now, now.Plus(Duration.FromHours(1)), null, null, false, null), Ct);
        await NewService().DeleteMeetingAsync(meetingId, member, Ct);

        // The tombstoned row is still readable, so a double-submitted form used to re-stamp it and
        // audit a second deletion — an event the trail would have claimed happened, and did not.
        var replay = async () => await NewService().DeleteMeetingAsync(meetingId, member, Ct);

        (await replay.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
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
        var meetingId = await NewService().CreateMeetingAsync(
            workgroup.Id, member,
            new WorkgroupMeetingSave("Sync", now, now.Plus(Duration.FromHours(1)), null, null, false, null), Ct);
        await NewService().DeleteMeetingAsync(meetingId, member, Ct);

        // Same unfiltered read, same class of bug: a deleted meeting must not be editable either.
        var edit = async () => await NewService().UpdateMeetingAsync(
            meetingId, member,
            new WorkgroupMeetingSave("Back from the dead", now, now.Plus(Duration.FromHours(1)),
                null, null, false, null), Ct);

        (await edit.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.NotFound);
        await AuditLog.DidNotReceive().LogAsync(
            AuditAction.WorkgroupMeetingUpdated, AuditEntityTypes.WorkgroupMeeting, meetingId,
            Arg.Any<string>(), member, Arg.Any<Guid?>(), Arg.Any<string?>());
    }
}
