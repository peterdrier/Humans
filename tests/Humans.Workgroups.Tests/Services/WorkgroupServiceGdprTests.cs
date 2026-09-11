using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Gdpr.Contracts;
using Humans.Notifications.Contracts;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Humans.Workgroups.Tests.Services;

/// <summary>
/// The GDPR surface of design §18 and §20: export slice shape, erasure keeps content
/// while dropping attribution, and the account-merge fold re-points ids and collapses
/// duplicate current memberships.
/// </summary>
public sealed class WorkgroupServiceGdprTests : WorkgroupsTestHarness
{
    [HumansFact]
    public async Task Export_ReturnsOneSlicePerRowType_NamedByGdprExportSections()
    {
        var workgroup = await SeedWorkgroupAsync();
        var author = workgroup.Members.Single().UserId;
        var document = await AddDocumentAsync(workgroup.Id, WorkgroupDocumentStatus.Published, body: "Body");
        await AddLogEntryAsync(workgroup.Id, WorkgroupLogKind.Update, authorUserId: author);
        var comment = await AddCommentAsync(document.Id, authorUserId: author);

        var slices = await NewService().ContributeForUserAsync(author, Ct);

        slices.Select(s => s.SectionName).Should().BeEquivalentTo(
        [
            GdprExportSections.WorkgroupMemberships,
            GdprExportSections.WorkgroupLogEntries,
            GdprExportSections.WorkgroupMeetings,
            GdprExportSections.WorkgroupDocuments,
            GdprExportSections.WorkgroupComments
        ]);
        slices.Should().OnlyContain(s => s.Data != null);
        comment.AuthorUserId.Should().Be(author);
    }

    [HumansFact]
    public async Task Export_IncludesCommentsThisPersonRespondedToOrHid_NotOnlyTheirOwn()
    {
        // The erasure nulls all three attribution columns, so the export has to show all three:
        // a responder or moderator's id is stored on somebody else's comment.
        var workgroup = await SeedWorkgroupAsync();
        var moderator = workgroup.Members.Single().UserId;
        var stranger = SeedUser("Stranger");
        var document = await AddDocumentAsync(workgroup.Id, WorkgroupDocumentStatus.Published, body: "Body");
        var theirs = await AddCommentAsync(document.Id, authorUserId: stranger);

        await using (var ctx = OpenContext())
        {
            var row = await ctx.Comments.SingleAsync(c => c.Id == theirs.Id, Ct);
            row.RespondedByUserId = moderator;
            row.HiddenByUserId = moderator;
            await ctx.SaveChangesAsync(Ct);
        }

        var slices = await NewService().ContributeForUserAsync(moderator, Ct);

        var comments = slices.Single(s =>
            string.Equals(s.SectionName, GdprExportSections.WorkgroupComments, StringComparison.Ordinal)).Data;
        comments.Should().NotBeNull();
        ((System.Collections.IEnumerable)comments!).Cast<object>().Should().ContainSingle();
    }

    [HumansFact]
    public async Task Erase_NullsAttribution_ButKeepsCommentAndLogBodies()
    {
        var workgroup = await SeedWorkgroupAsync();
        // Read the seeded coordinator before adding the second: AddMemberAsync appends to the
        // same tracked collection, so Single() afterwards would see two.
        var author = workgroup.Members.Single().UserId;
        var second = SeedUser("Second coordinator");
        await AddMemberAsync(workgroup.Id, second, WorkgroupMemberRole.Coordinator);
        var entry = await AddLogEntryAsync(workgroup.Id, WorkgroupLogKind.Update, authorUserId: author);
        var document = await AddDocumentAsync(workgroup.Id, WorkgroupDocumentStatus.Published, body: "Body");
        var comment = await AddCommentAsync(document.Id, authorUserId: author);
        // Mutate the entry/comment bodies through EF directly: the seeders don't set bodies.
        await using (var ctx = OpenContext())
        {
            var trackedEntry = await ctx.LogEntries.SingleAsync(e => e.Id == entry.Id, Ct);
            trackedEntry.Body = "What we decided and why";
            await ctx.SaveChangesAsync(Ct);
        }

        await NewService().EraseForUserAsync(author, Ct);

        await using var reload = OpenContext();
        (await reload.Members.CountAsync(m => m.UserId == author, Ct)).Should().Be(0);
        var reloadedEntry = await reload.LogEntries.SingleAsync(e => e.Id == entry.Id, Ct);
        reloadedEntry.AuthorUserId.Should().BeNull();
        reloadedEntry.Body.Should().Be("What we decided and why");
        var reloadedComment = await reload.Comments.SingleAsync(c => c.Id == comment.Id, Ct);
        reloadedComment.AuthorUserId.Should().BeNull();
        reloadedComment.Body.Should().Be(comment.Body);
    }

    [HumansFact]
    public async Task Erase_TheLastCoordinatorOfAnActiveGroup_NotifiesTheBoard()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Active);
        var onlyCoordinator = workgroup.Members.Single().UserId;

        await NewService().EraseForUserAsync(onlyCoordinator, Ct);

        await Notifications.Received(1).SendToRoleAsync(
            Arg.Any<NotificationSource>(),
            Arg.Any<NotificationClass>(),
            Arg.Any<NotificationPriority>(),
            Arg.Any<string>(), RoleNames.Board, Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Erase_WithASecondCoordinatorStillStanding_DoesNotNotifyTheBoard()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Active);
        var first = workgroup.Members.Single().UserId;
        var second = SeedUser("Second coordinator");
        await AddMemberAsync(workgroup.Id, second, WorkgroupMemberRole.Coordinator);

        await NewService().EraseForUserAsync(first, Ct);

        await Notifications.DidNotReceive().SendToRoleAsync(
            Arg.Any<NotificationSource>(),
            Arg.Any<NotificationClass>(),
            Arg.Any<NotificationPriority>(),
            Arg.Any<string>(), RoleNames.Board, Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Reassign_RePointsAttribution_AndCollapsesDuplicateMemberships()
    {
        var workgroup = await SeedWorkgroupAsync();
        var source = SeedUser("Old account");
        var target = SeedUser("New account");
        await AddMemberAsync(workgroup.Id, source, WorkgroupMemberRole.Member);
        await AddMemberAsync(workgroup.Id, target, WorkgroupMemberRole.Member);
        var entry = await AddLogEntryAsync(workgroup.Id, WorkgroupLogKind.Update, authorUserId: source);

        await NewService().ReassignAsync(source, target, SeedUser("Actor"), Clock.GetCurrentInstant(), Ct);

        await using var ctx = OpenContext();
        (await ctx.Members.CountAsync(m => m.WorkgroupId == workgroup.Id && m.LeftAt == null, Ct)).Should().Be(2); // coordinator + target
        (await ctx.Members.CountAsync(m => m.UserId == target && m.LeftAt == null, Ct)).Should().Be(1);
        (await ctx.LogEntries.SingleAsync(e => e.Id == entry.Id, Ct)).AuthorUserId.Should().Be(target);
    }
}
