using Xunit;
using AwesomeAssertions;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Services;
using Humans.Workgroups.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Workgroups.Tests.Services;

/// <summary>
/// The register's state machine (design §6 and §20): every allowed and disallowed
/// transition, the reasons requirement, and the Dormant freeze/unfreeze.
/// </summary>
public sealed class WorkgroupServiceLifecycleTests : WorkgroupsTestHarness
{
    // ── Register: Applied/Referred → Active ─────────────────────────────────

    [HumansTheory]
    [InlineData(nameof(WorkgroupStatus.Applied))]
    [InlineData(nameof(WorkgroupStatus.Referred))]
    public async Task Register_FromAppliedOrReferred_Succeeds(string fromName)
    {
        var from = Enum.Parse<WorkgroupStatus>(fromName);

        var workgroup = await SeedWorkgroupAsync(status: from, driveFolderId: null);

        await NewService().RegisterAsync(workgroup.Id, SeedUser("Secretary"), Ct);

        await using var ctx = OpenContext();
        var reloaded = await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct);
        reloaded.Status.Should().Be(WorkgroupStatus.Active);
        reloaded.DriveFolderId.Should().NotBeNullOrEmpty();
        reloaded.RegisteredAt.Should().NotBeNull();
    }

    [HumansTheory]
    [InlineData(nameof(WorkgroupStatus.Active))]
    [InlineData(nameof(WorkgroupStatus.Refused))]
    [InlineData(nameof(WorkgroupStatus.Withdrawn))]
    [InlineData(nameof(WorkgroupStatus.Dormant))]
    public async Task Register_FromAnyOtherStatus_Throws(string fromName)
    {
        var from = Enum.Parse<WorkgroupStatus>(fromName);

        var workgroup = await SeedWorkgroupAsync(status: from);

        var act = () => NewService().RegisterAsync(workgroup.Id, SeedUser(), Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.WrongStatus);
    }

    // ── Refer: Applied only ──────────────────────────────────────────────────

    [HumansFact]
    public async Task Refer_FromApplied_Succeeds()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Applied);

        await NewService().ReferAsync(workgroup.Id, SeedUser(), "Maybe a conflict", Ct);

        await using var ctx = OpenContext();
        (await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct)).Status
            .Should().Be(WorkgroupStatus.Referred);
    }

    [HumansTheory]
    [InlineData(nameof(WorkgroupStatus.Referred))]
    [InlineData(nameof(WorkgroupStatus.Active))]
    [InlineData(nameof(WorkgroupStatus.Dormant))]
    public async Task Refer_FromAnyOtherStatus_Throws(string fromName)
    {
        var from = Enum.Parse<WorkgroupStatus>(fromName);

        var workgroup = await SeedWorkgroupAsync(status: from);

        var act = () => NewService().ReferAsync(workgroup.Id, SeedUser(), null, Ct);

        await act.Should().ThrowAsync<WorkgroupRuleException>();
    }

    // ── Refuse: Applied/Referred, reasons required ──────────────────────────

    [HumansTheory]
    [InlineData(nameof(WorkgroupStatus.Applied))]
    [InlineData(nameof(WorkgroupStatus.Referred))]
    public async Task Refuse_FromAppliedOrReferred_WithReasons_Succeeds(string fromName)
    {
        var from = Enum.Parse<WorkgroupStatus>(fromName);

        var workgroup = await SeedWorkgroupAsync(status: from);

        await NewService().RefuseAsync(workgroup.Id, SeedUser(), "Duplicates an existing group", Ct);

        await using var ctx = OpenContext();
        var reloaded = await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct);
        reloaded.Status.Should().Be(WorkgroupStatus.Refused);
        reloaded.Reasons.Should().Be("Duplicates an existing group");
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Refuse_WithoutReasons_Throws(string? reasons)
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Applied);

        var act = () => NewService().RefuseAsync(workgroup.Id, SeedUser(), reasons!, Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.ReasonsRequired);
    }

    [HumansFact]
    public async Task Refuse_FromActive_Throws()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Active);

        var act = () => NewService().RefuseAsync(workgroup.Id, SeedUser(), "reasons", Ct);

        await act.Should().ThrowAsync<WorkgroupRuleException>();
    }

    // ── Withdraw: Active/Dormant, reasons required ──────────────────────────

    [HumansTheory]
    [InlineData(nameof(WorkgroupStatus.Active))]
    [InlineData(nameof(WorkgroupStatus.Dormant))]
    public async Task Withdraw_FromActiveOrDormant_WithReasons_Succeeds(string fromName)
    {
        var from = Enum.Parse<WorkgroupStatus>(fromName);

        var workgroup = await SeedWorkgroupAsync(status: from);

        await NewService().WithdrawAsync(workgroup.Id, SeedUser(), "No longer needed", Ct);

        await using var ctx = OpenContext();
        var reloaded = await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct);
        reloaded.Status.Should().Be(WorkgroupStatus.Withdrawn);
        reloaded.Reasons.Should().Be("No longer needed");
        reloaded.DormantReason.Should().BeNull();
    }

    [HumansFact]
    public async Task Withdraw_WithoutReasons_Throws()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Active);

        var act = () => NewService().WithdrawAsync(workgroup.Id, SeedUser(), " ", Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.ReasonsRequired);
    }

    [HumansTheory]
    [InlineData(nameof(WorkgroupStatus.Applied))]
    [InlineData(nameof(WorkgroupStatus.Referred))]
    [InlineData(nameof(WorkgroupStatus.Refused))]
    public async Task Withdraw_FromAnyOtherStatus_Throws(string fromName)
    {
        var from = Enum.Parse<WorkgroupStatus>(fromName);

        var workgroup = await SeedWorkgroupAsync(status: from);

        var act = () => NewService().WithdrawAsync(workgroup.Id, SeedUser(), "reasons", Ct);

        await act.Should().ThrowAsync<WorkgroupRuleException>();
    }

    // ── Close: Active only, reasons required, always Quiet ──────────────────

    [HumansFact]
    public async Task Close_FromActive_WithReasons_EndsAsDormantQuiet()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Active);

        await NewService().CloseAsync(workgroup.Id, SeedUser(), "Two months of silence", Ct);

        await using var ctx = OpenContext();
        var reloaded = await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct);
        reloaded.Status.Should().Be(WorkgroupStatus.Dormant);
        reloaded.DormantReason.Should().Be(WorkgroupDormantReason.Quiet);
    }

    [HumansFact]
    public async Task Close_WithoutReasons_Throws()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Active);

        var act = () => NewService().CloseAsync(workgroup.Id, SeedUser(), "", Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.ReasonsRequired);
    }

    [HumansTheory]
    [InlineData(nameof(WorkgroupStatus.Applied))]
    [InlineData(nameof(WorkgroupStatus.Dormant))]
    [InlineData(nameof(WorkgroupStatus.Withdrawn))]
    public async Task Close_FromAnyOtherStatus_Throws(string fromName)
    {
        var from = Enum.Parse<WorkgroupStatus>(fromName);

        var workgroup = await SeedWorkgroupAsync(status: from);

        var act = () => NewService().CloseAsync(workgroup.Id, SeedUser(), "reasons", Ct);

        await act.Should().ThrowAsync<WorkgroupRuleException>();
    }

    // ── Reactivate: Dormant only ─────────────────────────────────────────────

    [HumansFact]
    public async Task Reactivate_FromDormant_RestoresActive()
    {
        var workgroup = await SeedWorkgroupAsync(
            status: WorkgroupStatus.Dormant, dormantReason: WorkgroupDormantReason.Quiet);

        await NewService().ReactivateAsync(workgroup.Id, SeedUser(), Ct);

        await using var ctx = OpenContext();
        var reloaded = await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct);
        reloaded.Status.Should().Be(WorkgroupStatus.Active);
        reloaded.DormantReason.Should().BeNull();
        reloaded.EndedAt.Should().BeNull();
    }

    [HumansTheory]
    [InlineData(nameof(WorkgroupStatus.Active))]
    [InlineData(nameof(WorkgroupStatus.Applied))]
    [InlineData(nameof(WorkgroupStatus.Withdrawn))]
    public async Task Reactivate_FromAnyOtherStatus_Throws(string fromName)
    {
        var from = Enum.Parse<WorkgroupStatus>(fromName);

        var workgroup = await SeedWorkgroupAsync(status: from);

        var act = () => NewService().ReactivateAsync(workgroup.Id, SeedUser(), Ct);

        await act.Should().ThrowAsync<WorkgroupRuleException>();
    }

    // ── MarkDone: members only, Quiet never allowed ──────────────────────────

    [HumansTheory]
    [InlineData(nameof(WorkgroupDormantReason.Delivered))]
    [InlineData(nameof(WorkgroupDormantReason.Abandoned))]
    public async Task MarkDone_WithAllowedReason_EndsTheGroup(string reasonName)
    {
        var reason = Enum.Parse<WorkgroupDormantReason>(reasonName);

        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Active);
        var member = workgroup.Members.Single().UserId;

        await NewService().MarkDoneAsync(workgroup.Id, member, reason, Ct);

        await using var ctx = OpenContext();
        var reloaded = await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct);
        reloaded.Status.Should().Be(WorkgroupStatus.Dormant);
        reloaded.DormantReason.Should().Be(reason);
    }

    [HumansFact]
    public async Task MarkDone_WithQuietReason_Throws()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Active);
        var member = workgroup.Members.Single().UserId;

        var act = () => NewService().MarkDoneAsync(workgroup.Id, member, WorkgroupDormantReason.Quiet, Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.DoneReasonInvalid);
    }

    [HumansFact]
    public async Task MarkDone_WithACommentWindowStillOpen_Throws()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Active);
        var member = workgroup.Members.Single().UserId;
        var now = Clock.GetCurrentInstant();
        await AddDocumentAsync(
            workgroup.Id,
            status: WorkgroupDocumentStatus.Published,
            categories: ["Scope"],
            opensAt: now - Duration.FromHours(1),
            closesAt: now + Duration.FromHours(1));

        var act = () => NewService().MarkDoneAsync(workgroup.Id, member, WorkgroupDormantReason.Delivered, Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.CommentsStillOpen);
    }

    // ── Dormant freezes every member mutation ────────────────────────────────

    [HumansFact]
    public async Task DormantGroup_RejectsAddingALogEntry()
    {
        var workgroup = await SeedWorkgroupAsync(
            status: WorkgroupStatus.Dormant, dormantReason: WorkgroupDormantReason.Quiet);
        var member = workgroup.Members.Single().UserId;

        var act = () => NewService().AddLogEntryAsync(
            workgroup.Id, member,
            new WorkgroupLogEntrySave(WorkgroupLogKind.Update, Clock.GetCurrentInstant().InUtc().Date, null, "Body"),
            Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.Frozen);
    }

    [HumansFact]
    public async Task DormantGroup_RejectsCreatingAMeeting()
    {
        var workgroup = await SeedWorkgroupAsync(
            status: WorkgroupStatus.Dormant, dormantReason: WorkgroupDormantReason.Quiet);
        var member = workgroup.Members.Single().UserId;
        var now = Clock.GetCurrentInstant();

        var act = () => NewService().CreateMeetingAsync(
            workgroup.Id, member,
            new WorkgroupMeetingSave("Meeting", now, now.Plus(NodaTime.Duration.FromHours(1)), null, null, false, null),
            Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.Frozen);
    }

    [HumansFact]
    public async Task DormantGroup_RejectsCreatingADocument()
    {
        var workgroup = await SeedWorkgroupAsync(
            status: WorkgroupStatus.Dormant, dormantReason: WorkgroupDormantReason.Quiet);
        var member = workgroup.Members.Single().UserId;

        var act = () => NewService().CreateDocumentAsync(
            workgroup.Id, member, new WorkgroupDocumentSave("Title", WorkgroupDocumentKind.Deliverable, "Body"), Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.Frozen);
    }

    [HumansFact]
    public async Task DormantGroup_RejectsEditingTheRegister()
    {
        var workgroup = await SeedWorkgroupAsync(
            status: WorkgroupStatus.Dormant, dormantReason: WorkgroupDormantReason.Quiet);
        var member = workgroup.Members.Single().UserId;

        var act = () => NewService().EditRegisterAsync(
            workgroup.Id, member,
            new WorkgroupRegisterEdit("New name", "Purpose", "Deliverable",
                WorkgroupDeliverableKind.Report, WorkgroupAudience.Board, null, null),
            Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.Frozen);
    }

    [HumansFact]
    public async Task DormantGroup_RejectsRespondingToAComment()
    {
        var workgroup = await SeedWorkgroupAsync(
            status: WorkgroupStatus.Dormant, dormantReason: WorkgroupDormantReason.Quiet);
        var member = workgroup.Members.Single().UserId;
        var document = await AddDocumentAsync(workgroup.Id, WorkgroupDocumentStatus.Delivered, deliveredAt: Clock.GetCurrentInstant());
        var comment = await AddCommentAsync(document.Id);

        var act = () => NewService().RespondToCommentAsync(
            comment.Id, member, WorkgroupCommentDisposition.Accepted, "Thanks", Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.Frozen);
    }

    [HumansFact]
    public async Task Reactivate_UnfreezesMemberWork()
    {
        var workgroup = await SeedWorkgroupAsync(
            status: WorkgroupStatus.Dormant, dormantReason: WorkgroupDormantReason.Quiet);
        var member = workgroup.Members.Single().UserId;
        var service = NewService();
        await service.ReactivateAsync(workgroup.Id, SeedUser(), Ct);

        await service.AddLogEntryAsync(
            workgroup.Id, member,
            new WorkgroupLogEntrySave(WorkgroupLogKind.Note, Clock.GetCurrentInstant().InUtc().Date, null, "Back at it"),
            Ct);

        await using var ctx = OpenContext();
        (await ctx.LogEntries.CountAsync(e => e.WorkgroupId == workgroup.Id && e.Kind == WorkgroupLogKind.Note, Ct))
            .Should().Be(1);
    }

    // ── The Dormant freeze reaches comments too ──────────────────────────────

    /// <summary>
    /// Design §5 lists comments among the member mutations a Dormant group rejects. Close and
    /// Withdraw leave an already-open comment window alone, so without the status check in
    /// <c>AddCommentAsync</c> anyone could keep commenting on a closed group's document.
    /// </summary>
    [HumansFact]
    public async Task AddComment_OnDormantGroupWithStillOpenWindow_IsFrozen()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Active);
        var now = Clock.GetCurrentInstant();
        var document = await AddDocumentAsync(
            workgroup.Id, WorkgroupDocumentStatus.Published, categories: ["Scope"],
            opensAt: now.Minus(NodaTime.Duration.FromDays(1)), closesAt: now.Plus(NodaTime.Duration.FromDays(1)));

        // The Secretary closes the group for silence while the comment window is still open.
        await NewService().CloseAsync(workgroup.Id, SeedUser(), "Quiet for two months", Ct);

        var commenter = SeedUser("A member of the public");
        var act = () => NewService().AddCommentAsync(document.Id, commenter, "Scope", "Still allowed?", Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.Frozen);
    }
}
