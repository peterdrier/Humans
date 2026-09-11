using Xunit;
using AwesomeAssertions;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Services;
using Humans.Workgroups.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Workgroups.Tests.Services;

/// <summary>
/// Documents and their comment period (design §12, §20): publish, the comment window,
/// delivery freezing the body, disposition, and the bulk category response.
/// </summary>
public sealed class WorkgroupServiceDocumentTests : WorkgroupsTestHarness
{
    // ── Publish ───────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Publish_ADraftWithABody_Succeeds()
    {
        var workgroup = await SeedWorkgroupAsync();
        var member = workgroup.Members.Single().UserId;
        var document = await AddDocumentAsync(workgroup.Id, body: "Something to publish");

        await NewService().PublishDocumentAsync(document.Id, member, Ct);

        await using var ctx = OpenContext();
        (await ctx.Documents.SingleAsync(d => d.Id == document.Id, Ct)).Status
            .Should().Be(WorkgroupDocumentStatus.Published);
    }

    [HumansFact]
    public async Task Publish_AnEmptyBody_Throws()
    {
        var workgroup = await SeedWorkgroupAsync();
        var member = workgroup.Members.Single().UserId;
        var document = await AddDocumentAsync(workgroup.Id, body: "");

        var act = () => NewService().PublishDocumentAsync(document.Id, member, Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.BodyRequired);
    }

    [HumansFact]
    public async Task Publish_AlreadyPublished_Throws()
    {
        var workgroup = await SeedWorkgroupAsync();
        var member = workgroup.Members.Single().UserId;
        var document = await AddDocumentAsync(workgroup.Id, WorkgroupDocumentStatus.Published, body: "Body");

        var act = () => NewService().PublishDocumentAsync(document.Id, member, Ct);

        await act.Should().ThrowAsync<WorkgroupRuleException>();
    }

    // ── Comment window ────────────────────────────────────────────────────

    [HumansFact]
    public async Task OpenComments_OnADraft_Throws()
    {
        var workgroup = await SeedWorkgroupAsync();
        var member = workgroup.Members.Single().UserId;
        var document = await AddDocumentAsync(workgroup.Id);
        var now = Clock.GetCurrentInstant();

        var act = () => NewService().OpenCommentsAsync(
            document.Id, member, new WorkgroupCommentWindow(now, now.Plus(Duration.FromDays(1)), ["Scope"]), Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.NotPublished);
    }

    [HumansFact]
    public async Task OpenComments_WithNoCategories_Throws()
    {
        var workgroup = await SeedWorkgroupAsync();
        var member = workgroup.Members.Single().UserId;
        var document = await AddDocumentAsync(workgroup.Id, WorkgroupDocumentStatus.Published, body: "Body");
        var now = Clock.GetCurrentInstant();

        var act = () => NewService().OpenCommentsAsync(
            document.Id, member, new WorkgroupCommentWindow(now, now.Plus(Duration.FromDays(1)), []), Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.CategoriesRequired);
    }

    [HumansFact]
    public async Task OpenComments_ClosingBeforeItOpens_Throws()
    {
        var workgroup = await SeedWorkgroupAsync();
        var member = workgroup.Members.Single().UserId;
        var document = await AddDocumentAsync(workgroup.Id, WorkgroupDocumentStatus.Published, body: "Body");
        var now = Clock.GetCurrentInstant();

        var act = () => NewService().OpenCommentsAsync(
            document.Id, member, new WorkgroupCommentWindow(now, now, ["Scope"]), Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.WindowInvalid);
    }

    [HumansFact]
    public async Task OpenComments_OnAPublishedDocumentWithCategories_Succeeds()
    {
        var workgroup = await SeedWorkgroupAsync();
        var member = workgroup.Members.Single().UserId;
        var document = await AddDocumentAsync(workgroup.Id, WorkgroupDocumentStatus.Published, body: "Body");
        var now = Clock.GetCurrentInstant();

        await NewService().OpenCommentsAsync(
            document.Id, member,
            new WorkgroupCommentWindow(now, now.Plus(Duration.FromDays(7)), ["Scope", "Wording"]), Ct);

        await using var ctx = OpenContext();
        var reloaded = await ctx.Documents.SingleAsync(d => d.Id == document.Id, Ct);
        reloaded.CommentCategories.Should().BeEquivalentTo(["Scope", "Wording"]);
        reloaded.CommentsOpenAt.Should().Be(now);
    }

    // ── Delivering freezes the body ──────────────────────────────────────

    [HumansFact]
    public async Task Deliver_APublishedDocument_FreezesTheBody()
    {
        var workgroup = await SeedWorkgroupAsync();
        var member = workgroup.Members.Single().UserId;
        var document = await AddDocumentAsync(workgroup.Id, WorkgroupDocumentStatus.Published, body: "Body");

        await NewService().DeliverDocumentAsync(document.Id, member, Ct);

        var act = () => NewService().UpdateDocumentAsync(
            document.Id, member, new WorkgroupDocumentSave("New title", WorkgroupDocumentKind.Deliverable, "Edited"), Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.DocumentFrozen);
    }

    [HumansFact]
    public async Task Deliver_ADraft_Throws()
    {
        var workgroup = await SeedWorkgroupAsync();
        var member = workgroup.Members.Single().UserId;
        var document = await AddDocumentAsync(workgroup.Id);

        var act = () => NewService().DeliverDocumentAsync(document.Id, member, Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.NotPublished);
    }

    [HumansFact]
    public async Task Deliver_WhileTheCommentWindowIsStillOpen_Throws()
    {
        // "A comment window ... must end before Delivered" (design §7): delivering mid-window
        // would freeze the body and cut a promised public comment period short.
        var workgroup = await SeedWorkgroupAsync();
        var member = workgroup.Members.Single().UserId;
        var now = Clock.GetCurrentInstant();
        var document = await AddDocumentAsync(
            workgroup.Id, WorkgroupDocumentStatus.Published, body: "Body",
            categories: ["Scope"], opensAt: now, closesAt: now + Duration.FromDays(7));

        var act = () => NewService().DeliverDocumentAsync(document.Id, member, Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.CommentsStillOpen);
    }

    [HumansFact]
    public async Task Deliver_AfterTheCommentWindowHasClosed_Succeeds()
    {
        var workgroup = await SeedWorkgroupAsync();
        var member = workgroup.Members.Single().UserId;
        var now = Clock.GetCurrentInstant();
        var document = await AddDocumentAsync(
            workgroup.Id, WorkgroupDocumentStatus.Published, body: "Body",
            categories: ["Scope"], opensAt: now - Duration.FromDays(7), closesAt: now - Duration.FromHours(1));

        await NewService().DeliverDocumentAsync(document.Id, member, Ct);

        await using var ctx = OpenContext();
        (await ctx.Documents.SingleAsync(d => d.Id == document.Id, Ct))
            .Status.Should().Be(WorkgroupDocumentStatus.Delivered);
    }

    // ── Disposition only on Delivered ────────────────────────────────────

    [HumansFact]
    public async Task RecordDisposition_OnAPublishedButNotDeliveredDocument_Throws()
    {
        var workgroup = await SeedWorkgroupAsync();
        var document = await AddDocumentAsync(workgroup.Id, WorkgroupDocumentStatus.Published, body: "Body");

        var act = () => NewService().RecordDispositionAsync(
            document.Id, SeedUser(), WorkgroupDisposition.Accepted, "Great work", Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.NotDelivered);
    }

    [HumansFact]
    public async Task RecordDisposition_OnADeliveredDocument_Succeeds()
    {
        var workgroup = await SeedWorkgroupAsync();
        var document = await AddDocumentAsync(
            workgroup.Id, WorkgroupDocumentStatus.Delivered, deliveredAt: Clock.GetCurrentInstant());

        await NewService().RecordDispositionAsync(
            document.Id, SeedUser(), WorkgroupDisposition.Accepted, "Great work", Ct);

        await using var ctx = OpenContext();
        var reloaded = await ctx.Documents.SingleAsync(d => d.Id == document.Id, Ct);
        reloaded.Disposition.Should().Be(WorkgroupDisposition.Accepted);
        reloaded.DispositionNote.Should().Be("Great work");
    }

    [HumansFact]
    public async Task RecordDisposition_WithoutANote_Throws()
    {
        var workgroup = await SeedWorkgroupAsync();
        var document = await AddDocumentAsync(
            workgroup.Id, WorkgroupDocumentStatus.Delivered, deliveredAt: Clock.GetCurrentInstant());

        var act = () => NewService().RecordDispositionAsync(
            document.Id, SeedUser(), WorkgroupDisposition.Accepted, " ", Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.ReasonsRequired);
    }

    // ── Comments: category and window gating ─────────────────────────────

    [HumansFact]
    public async Task AddComment_UnknownCategory_Throws()
    {
        var workgroup = await SeedWorkgroupAsync();
        var now = Clock.GetCurrentInstant();
        var document = await AddDocumentAsync(
            workgroup.Id, WorkgroupDocumentStatus.Published, categories: ["Scope"],
            opensAt: now.Minus(Duration.FromHours(1)), closesAt: now.Plus(Duration.FromDays(1)));

        var act = () => NewService().AddCommentAsync(document.Id, SeedUser(), "Not-a-category", "Body", Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.UnknownCategory);
    }

    [HumansTheory]
    [InlineData(-2, -1)] // window already closed
    [InlineData(1, 2)]   // window not open yet
    public async Task AddComment_OutsideTheWindow_Throws(int openOffsetDays, int closeOffsetDays)
    {
        var workgroup = await SeedWorkgroupAsync();
        var now = Clock.GetCurrentInstant();
        var document = await AddDocumentAsync(
            workgroup.Id, WorkgroupDocumentStatus.Published, categories: ["Scope"],
            opensAt: now.Plus(Duration.FromDays(openOffsetDays)),
            closesAt: now.Plus(Duration.FromDays(closeOffsetDays)));

        var act = () => NewService().AddCommentAsync(document.Id, SeedUser(), "Scope", "Body", Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.CommentsClosed);
    }

    [HumansFact]
    public async Task AddComment_InsideTheWindow_Succeeds()
    {
        var workgroup = await SeedWorkgroupAsync();
        var now = Clock.GetCurrentInstant();
        var document = await AddDocumentAsync(
            workgroup.Id, WorkgroupDocumentStatus.Published, categories: ["Scope"],
            opensAt: now.Minus(Duration.FromHours(1)), closesAt: now.Plus(Duration.FromDays(1)));

        var id = await NewService().AddCommentAsync(document.Id, SeedUser(), "Scope", "Looks good", Ct);

        await using var ctx = OpenContext();
        (await ctx.Comments.SingleAsync(c => c.Id == id, Ct)).Disposition
            .Should().Be(WorkgroupCommentDisposition.Pending);
    }

    // ── Bulk category response touches only Pending comments ────────────

    [HumansFact]
    public async Task RespondToCategory_LeavesAlreadyAnsweredCommentsUntouched()
    {
        var workgroup = await SeedWorkgroupAsync();
        var member = workgroup.Members.Single().UserId;
        var document = await AddDocumentAsync(
            workgroup.Id, WorkgroupDocumentStatus.Published, categories: ["Scope", "Wording"]);
        var alreadyAnswered = await AddCommentAsync(
            document.Id, category: "Scope", disposition: WorkgroupCommentDisposition.Incorporated);
        var pendingScope = await AddCommentAsync(document.Id, category: "Scope");
        var pendingOtherCategory = await AddCommentAsync(document.Id, category: "Wording");

        await NewService().RespondToCategoryAsync(
            document.Id, member, "Scope", WorkgroupCommentDisposition.Accepted, "Thanks all", Ct);

        await using var ctx = OpenContext();
        (await ctx.Comments.SingleAsync(c => c.Id == alreadyAnswered.Id, Ct)).Disposition
            .Should().Be(WorkgroupCommentDisposition.Incorporated, "an already-answered comment keeps its own answer");
        (await ctx.Comments.SingleAsync(c => c.Id == pendingScope.Id, Ct)).Disposition
            .Should().Be(WorkgroupCommentDisposition.Accepted);
        (await ctx.Comments.SingleAsync(c => c.Id == pendingOtherCategory.Id, Ct)).Disposition
            .Should().Be(WorkgroupCommentDisposition.Pending, "a different category is untouched by the bulk pass");
    }

    [HumansFact]
    public async Task RespondToCategory_SkipsHiddenComments()
    {
        var workgroup = await SeedWorkgroupAsync();
        var member = workgroup.Members.Single().UserId;
        var document = await AddDocumentAsync(
            workgroup.Id, WorkgroupDocumentStatus.Published, categories: ["Scope"]);
        var hidden = await AddCommentAsync(document.Id, category: "Scope");
        await NewService().HideCommentAsync(hidden.Id, member, "Off-topic", Ct);

        await NewService().RespondToCategoryAsync(
            document.Id, member, "Scope", WorkgroupCommentDisposition.Accepted, "Thanks", Ct);

        await using var ctx = OpenContext();
        (await ctx.Comments.SingleAsync(c => c.Id == hidden.Id, Ct)).Disposition
            .Should().Be(WorkgroupCommentDisposition.Pending);
    }
}
