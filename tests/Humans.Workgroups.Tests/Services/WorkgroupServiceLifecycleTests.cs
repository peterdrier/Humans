using Xunit;
using NSubstitute;
using AwesomeAssertions;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Services;
using Humans.Workgroups.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Workgroups.Data;

namespace Humans.Workgroups.Tests.Services;

/// <summary>
/// The register's state machine (design §6 and §20): every allowed and disallowed
/// transition, the reasons requirement, and the Dormant freeze/unfreeze.
/// </summary>
public sealed class WorkgroupServiceLifecycleTests : WorkgroupsTestHarness
{
    [HumansTheory]
    [InlineData("refer")]
    [InlineData("refuse")]
    [InlineData("withdraw")]
    [InlineData("close")]
    [InlineData("done")]
    [InlineData("reactivate")]
    [InlineData("disposition")]
    public async Task Lifecycle_CommittedWriteFinishesAfterRequestCancellation(string action)
    {
        var status = action switch
        {
            "refer" or "refuse" => WorkgroupStatus.Applied,
            "reactivate" => WorkgroupStatus.Dormant,
            _ => WorkgroupStatus.Active
        };
        var group = await SeedWorkgroupAsync(status: status);
        group.HoldedAccountNumber = 62900160;
        await Db.SaveChangesAsync(Ct);
        var actor = group.Members.Single().UserId;
        var document = await AddDocumentAsync(group.Id, WorkgroupDocumentStatus.Delivered);
        using var cancellation = new CancellationTokenSource();
        var real = new WorkgroupRepository(DbFactory);
        var repository = Substitute.For<IWorkgroupRepository>();
        repository.GetWorkgroupAsync(group.Id, Arg.Any<CancellationToken>())
            .Returns(c => real.GetWorkgroupAsync(group.Id, c.Arg<CancellationToken>()));
        repository.GetDocumentAsync(document.Id, Arg.Any<CancellationToken>())
            .Returns(c => real.GetDocumentAsync(document.Id, c.Arg<CancellationToken>()));
        repository.UpdateWorkgroupAsync(Arg.Any<Workgroup>(), Arg.Any<CancellationToken>())
            .Returns(async c =>
            {
                c.Arg<CancellationToken>().Should().Be(cancellation.Token);
                await real.UpdateWorkgroupAsync(c.Arg<Workgroup>(), c.Arg<CancellationToken>());
                await cancellation.CancelAsync();
            });
        repository.UpdateDocumentAsync(Arg.Any<WorkgroupDocument>(), Arg.Any<CancellationToken>())
            .Returns(async c =>
            {
                c.Arg<CancellationToken>().Should().Be(cancellation.Token);
                await real.UpdateDocumentAsync(c.Arg<WorkgroupDocument>(), c.Arg<CancellationToken>());
                await cancellation.CancelAsync();
            });
        repository.AddLogEntryAsync(Arg.Any<WorkgroupLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(c => real.AddLogEntryAsync(c.Arg<WorkgroupLogEntry>(), c.Arg<CancellationToken>()));
        Roles.GetActiveUserIdsInRoleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([actor]);
        var service = NewService(repository);
        Func<Task> mutate = action switch
        {
            "refer" => () => service.ReferAsync(group.Id, actor, "Review", cancellation.Token),
            "refuse" => () => service.RefuseAsync(group.Id, actor, "Refused", cancellation.Token),
            "withdraw" => () => service.WithdrawAsync(group.Id, actor, "Withdrawn", cancellation.Token),
            "close" => () => service.CloseAsync(group.Id, actor, "Quiet", cancellation.Token),
            "done" => () => service.MarkDoneAsync(group.Id, actor, WorkgroupDormantReason.Delivered, cancellation.Token),
            "reactivate" => () => service.ReactivateAsync(group.Id, actor, cancellation.Token),
            _ => () => service.RecordDispositionAsync(document.Id, actor, WorkgroupDisposition.Accepted, "Accepted", cancellation.Token)
        };

        await mutate.Should().NotThrowAsync();

        await using var db = OpenContext();
        (await db.LogEntries.CountAsync(e => e.WorkgroupId == group.Id, Ct)).Should().Be(1);
        await repository.DidNotReceive().AddLogEntryAsync(Arg.Any<WorkgroupLogEntry>(), cancellation.Token);
        AuditLog.ReceivedCalls().Should().ContainSingle();
        Notifications.ReceivedCalls().Should().ContainSingle().Which.GetArguments()
            .OfType<CancellationToken>().Should().ContainSingle().Which.Should().Be(CancellationToken.None);
        Email.ReceivedCalls().Should().ContainSingle().Which.GetArguments()
            .OfType<CancellationToken>().Should().ContainSingle().Which.Should().Be(CancellationToken.None);
        if (action is "withdraw" or "close" or "done" or "reactivate")
            await Finance.Received(1).SetExpenseAccountActiveAsync(62900160,
                string.Equals(action, "reactivate", StringComparison.Ordinal), CancellationToken.None);
    }

    [HumansTheory]
    [InlineData("apply")]
    [InlineData("refer")]
    [InlineData("refuse")]
    public async Task Notices_LongGroupNameAndDetail_FitStorageAndKeepFullRecords(string action)
    {
        var name = new string('x', 200);
        var detail = string.Concat(Enumerable.Repeat("🚀", 1998)) + "tail";
        Guid id;
        string fullTitle;
        if (string.Equals(action, "apply", StringComparison.Ordinal))
        {
            id = await NewService().ApplyAsync(SeedUser(), new WorkgroupApplication(
                name, "Purpose", "Report", WorkgroupDeliverableKind.Report,
                WorkgroupAudience.Board, null, null, null), Ct);
            fullTitle = $"Working group applied: {name}";
        }
        else
        {
            var group = await SeedWorkgroupAsync(status: WorkgroupStatus.Applied, name: name);
            group.Slug = "boundary-group";
            await Db.SaveChangesAsync(Ct);
            id = group.Id;
            if (string.Equals(action, "refer", StringComparison.Ordinal))
            {
                await NewService().ReferAsync(id, SeedUser(), detail, Ct);
                fullTitle = $"Referred to the Board: {name}";
            }
            else
            {
                await NewService().RefuseAsync(id, SeedUser(), detail, Ct);
                fullTitle = $"Refused: {name}";
            }
        }

        var args = Notifications.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
        ((string)args[3]!).Should().Be(string.Concat(fullTitle.EnumerateRunes().Take(199)) + "…");
        var body = (string)args[5]!;
        body.Should().StartWith(fullTitle + "\n\n");
        body.EnumerateRunes().Count().Should().BeLessThanOrEqualTo(2000);
        body.Should().NotContain("�");
        await using var ctx = OpenContext();
        var stored = await ctx.Workgroups.SingleAsync(w => w.Id == id, Ct);
        stored.Name.Should().Be(name);
        if (!string.Equals(action, "apply", StringComparison.Ordinal))
        {
            body.EnumerateRunes().Count().Should().Be(2000);
            body.Should().EndWith("…");
            (await ctx.LogEntries.SingleAsync(e => e.WorkgroupId == id, Ct)).Body.Should().Be(detail);
        }
        if (string.Equals(action, "refuse", StringComparison.Ordinal))
            stored.Reasons.Should().Be(detail);
    }

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
