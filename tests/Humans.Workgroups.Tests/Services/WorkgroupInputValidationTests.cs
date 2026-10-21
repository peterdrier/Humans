using AwesomeAssertions;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Models;
using Humans.Workgroups.Services;
using Humans.Workgroups.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Workgroups.Tests.Services;

public sealed class WorkgroupInputValidationTests : WorkgroupsTestHarness
{
    [HumansTheory]
    [InlineData("apply", "javascript:alert(1)")]
    [InlineData("existing", "javascript:alert(1)")]
    [InlineData("edit", "javascript:alert(1)")]
    [InlineData("apply", "data:text/html,hello")]
    [InlineData("apply", "//discord.com/channels/123")]
    [InlineData("apply", "ftp://example.org/channel")]
    public async Task DiscordLink_RejectsUnsafeUrlsBeforeMutation(string path, string url)
    {
        var group = await SeedWorkgroupAsync();
        var actor = group.Members.Single().UserId;
        var service = NewService();
        var application = new WorkgroupApplication("New group", "Purpose", "Report",
            WorkgroupDeliverableKind.Report, WorkgroupAudience.Board, null, url, null);
        WorkgroupMutationResult result;
        if (string.Equals(path, "edit", StringComparison.Ordinal))
            result = await service.EditRegisterAsync(group.Id, actor,
                new WorkgroupRegisterEdit(application.Name, application.Purpose, application.Deliverable,
                    application.DeliverableKind, application.Audience, null, url), Ct);
        else if (string.Equals(path, "existing", StringComparison.Ordinal))
            result = await service.RegisterExistingAsync(actor,
                new WorkgroupBootstrap(application, actor, Clock.GetCurrentInstant()), Ct);
        else
            result = await service.ApplyAsync(actor, application, Ct);

        result.Refusal.Should().NotBeNull();
        result.Refusal!.Key.Should().Be("Workgroups_Error_DiscordUrlInvalid");
        await using var db = OpenContext();
        var saved = (await db.Workgroups.ToListAsync(Ct)).Should().ContainSingle().Subject;
        saved.Name.Should().Be(group.Name);
        saved.DiscordChannelUrl.Should().Be(group.DiscordChannelUrl);
        AuditLog.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("data:text/html,hello", null)]
    [InlineData("https://discord.com/channels/123", "https://discord.com/channels/123")]
    [InlineData(" http://example.org/channel ", "http://example.org/channel")]
    public async Task DiscordLink_ProjectionOmitsUnsafeLegacyValuesWithoutChangingStorage(string url, string? expected)
    {
        var group = await SeedWorkgroupAsync();
        await using (var db = OpenContext())
        {
            var saved = await db.Workgroups.SingleAsync(Ct);
            saved.DiscordChannelUrl = url;
            await db.SaveChangesAsync(Ct);
        }

        var info = await NewService().GetByIdAsync(group.Id, Ct);

        info!.DiscordChannelUrl.Should().Be(expected);
        await using var check = OpenContext();
        (await check.Workgroups.SingleAsync(Ct)).DiscordChannelUrl.Should().Be(url);
    }

    [HumansFact]
    public void MeetingForm_AcceptsAConferencingUrlAtTheStorageLimit()
    {
        var model = new MeetingFormViewModel
        {
            Slug = "planning",
            Title = "Meeting",
            LocationUrl = "https://example.org/".PadRight(2000, 'x')
        };
        var errors = new List<ValidationResult>();

        Validator.TryValidateObject(model, new ValidationContext(model), errors, validateAllProperties: true)
            .Should().BeTrue();
    }

    // <input type="number"> posts "12.34" in every culture; a comma-decimal request culture must
    // not read it as 1234.
    [HumansTheory]
    [InlineData("es-ES")]
    [InlineData("de-DE")]
    public void BudgetForm_ParsesAmountInvariantlyWhateverTheRequestCulture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var model = new WorkgroupBudgetFormViewModel { HasBudget = true, Amount = "12.34" };

            model.ToSave().Amount.Should().Be(12.34m);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [HumansTheory]
    [InlineData(true, null, "create", null, false)]
    [InlineData(true, "100", "link", null, false)]
    [InlineData(true, "100", "link", 62900170, true)]
    [InlineData(true, "100", "create", null, true)]
    [InlineData(true, "abc", "create", null, false)]
    [InlineData(true, "-1", "create", null, false)]
    [InlineData(false, null, "link", null, true)]
    public void BudgetForm_RequiresAmountAndLinkedAccountWhenBudgeted(
        bool hasBudget, string? amount, string mode, int? account, bool valid)
    {
        var model = new WorkgroupBudgetFormViewModel
        {
            HasBudget = hasBudget,
            Amount = amount,
            AccountMode = mode,
            ExistingAccountNum = account
        };
        var errors = new List<ValidationResult>();

        Validator.TryValidateObject(model, new ValidationContext(model), errors, validateAllProperties: true)
            .Should().Be(valid);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogBody_RejectsOverflowWithoutChangingStoredEntries(bool editing)
    {
        var group = await SeedWorkgroupAsync();
        var service = NewService();
        var actor = group.Members.Single().UserId;
        var original = new string('x', 16000);
        var save = new WorkgroupLogEntrySave(WorkgroupLogKind.Update, Clock.GetCurrentInstant().InUtc().Date, null, original);
        var entryId = (await service.AddLogEntryAsync(group.Id, actor, save, Ct)).Value;

        Func<Task<WorkgroupMutationResult>> act = async () =>
        {
            if (editing)
                return await service.UpdateLogEntryAsync(entryId, actor, save with { Body = original + "x" }, Ct);
            else
                return await service.AddLogEntryAsync(group.Id, actor, save with { Body = original + "x" }, Ct);
        };

        var refusal = (await act()).Refusal;
        refusal.Should().NotBeNull();
        refusal.Key.Should().Be(WorkgroupErrorKeys.TextTooLong);
        refusal.Args.Should().Equal(16000);
        await using var ctx = OpenContext();
        var entries = await ctx.LogEntries.Where(e => e.WorkgroupId == group.Id).ToListAsync(Ct);
        entries.Should().ContainSingle().Which.Body.Should().Be(original);
    }

    [HumansTheory]
    [InlineData("Refuse")]
    [InlineData("Withdraw")]
    [InlineData("Close")]
    public async Task LifecycleReasons_RejectOverflowBeforeChangingTheGroup(string action)
    {
        var initialStatus = string.Equals(action, "Refuse", StringComparison.Ordinal)
            ? WorkgroupStatus.Applied : WorkgroupStatus.Active;
        var group = await SeedWorkgroupAsync(status: initialStatus);
        var service = NewService();
        var actor = SeedUser();
        var reason = new string('x', 4001);
        var act = () => action switch
        {
            "Refuse" => service.RefuseAsync(group.Id, actor, reason, Ct),
            "Withdraw" => service.WithdrawAsync(group.Id, actor, reason, Ct),
            _ => service.CloseAsync(group.Id, actor, reason, Ct)
        };

        var refusal = (await act()).Refusal;
        refusal.Should().NotBeNull();
        refusal.Key.Should().Be(WorkgroupErrorKeys.TextTooLong);
        refusal.Args.Should().Equal(4000);
        await using var ctx = OpenContext();
        var stored = await ctx.Workgroups.SingleAsync(w => w.Id == group.Id, Ct);
        stored.Status.Should().Be(initialStatus);
        stored.Reasons.Should().BeNull();
        (await ctx.LogEntries.AnyAsync(e => e.WorkgroupId == group.Id, Ct)).Should().BeFalse();
    }

    [HumansFact]
    public async Task LifecycleReasons_AcceptsTheStorageLimitAfterTrimming()
    {
        var group = await SeedWorkgroupAsync(status: WorkgroupStatus.Applied);
        var reason = new string('x', 4000);

        await NewService().RefuseAsync(group.Id, SeedUser(), "  " + reason + "  ", Ct);

        await using var ctx = OpenContext();
        (await ctx.Workgroups.SingleAsync(w => w.Id == group.Id, Ct)).Reasons.Should().Be(reason);
    }

    [HumansTheory]
    [InlineData("Disposition", false)]
    [InlineData("Disposition", true)]
    [InlineData("Comment", false)]
    [InlineData("Comment", true)]
    [InlineData("Response", false)]
    [InlineData("Response", true)]
    [InlineData("CategoryResponse", false)]
    [InlineData("CategoryResponse", true)]
    [InlineData("Hide", false)]
    [InlineData("Hide", true)]
    public async Task DocumentText_EnforcesTheStorageLimitAfterTrimming(string action, bool overflow)
    {
        var group = await SeedWorkgroupAsync();
        var actor = group.Members.Single().UserId;
        var now = Clock.GetCurrentInstant();
        var document = await AddDocumentAsync(group.Id,
            status: action is "Disposition" ? WorkgroupDocumentStatus.Delivered : WorkgroupDocumentStatus.Published,
            categories: ["Scope"], opensAt: now - Duration.FromHours(1), closesAt: now + Duration.FromHours(1));
        var comment = await AddCommentAsync(document.Id, authorUserId: actor);
        var second = await AddCommentAsync(document.Id, authorUserId: actor);
        var limit = action is "Hide" ? 500 : 4000;
        var content = new string('x', limit + (overflow ? 1 : 0));
        var text = "  " + content + "  ";
        var service = NewService();

        WorkgroupMutationResult result = action switch
        {
            "Disposition" => await service.RecordDispositionAsync(document.Id, actor, WorkgroupDisposition.Accepted, text, Ct),
            "Comment" => await service.AddCommentAsync(document.Id, actor, "Scope", text, Ct),
            "Response" => await service.RespondToCommentAsync(comment.Id, actor, WorkgroupCommentDisposition.Accepted, text, Ct),
            "CategoryResponse" => await service.RespondToCategoryAsync(document.Id, actor, "Scope", WorkgroupCommentDisposition.Accepted, text, Ct),
            _ => await service.HideCommentAsync(comment.Id, actor, text, Ct)
        };

        if (overflow)
        {
            result.Refusal.Should().NotBeNull();
            result.Refusal!.Key.Should().Be(WorkgroupErrorKeys.TextTooLong);
            result.Refusal.Args.Should().Equal(limit);
        }
        else
            result.Refusal.Should().BeNull();

        await using var ctx = OpenContext();
        var storedDocument = await ctx.Documents.SingleAsync(d => d.Id == document.Id, Ct);
        var storedComment = await ctx.Comments.SingleAsync(c => c.Id == comment.Id, Ct);
        var storedSecond = await ctx.Comments.SingleAsync(c => c.Id == second.Id, Ct);
        if (action is "Disposition")
        {
            storedDocument.DispositionNote.Should().Be(overflow ? null : content);
            storedDocument.Disposition.Should().Be(overflow ? null : WorkgroupDisposition.Accepted);
        }
        else if (action is "Comment")
            (await ctx.Comments.CountAsync(c => c.DocumentId == document.Id, Ct)).Should().Be(overflow ? 2 : 3);
        else if (action is "Hide")
        {
            storedComment.HiddenReason.Should().Be(overflow ? null : content);
            storedComment.HiddenAt.HasValue.Should().Be(!overflow);
        }
        else
        {
            storedComment.Response.Should().Be(overflow ? null : content);
            storedComment.Disposition.Should().Be(overflow ? WorkgroupCommentDisposition.Pending : WorkgroupCommentDisposition.Accepted);
            storedSecond.Response.Should().Be(!overflow && action is "CategoryResponse" ? content : null);
        }
        if (overflow)
        {
            (await ctx.LogEntries.AnyAsync(e => e.WorkgroupId == group.Id, Ct)).Should().BeFalse();
            AuditLog.ReceivedCalls().Should().BeEmpty();
            Notifications.ReceivedCalls().Should().BeEmpty();
            Email.ReceivedCalls().Should().BeEmpty();
        }
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MeetingUrl_AcceptsTheStorageLimitAndRejectsOverflow(bool editing)
    {
        var group = await SeedWorkgroupAsync();
        var service = NewService();
        var now = Clock.GetCurrentInstant();
        var actor = group.Members.Single().UserId;
        var url = "https://example.org/".PadRight(2000, 'x');
        var save = new WorkgroupMeetingSave("Meeting", now, now.Plus(Duration.FromHours(1)), null, url, false, null);
        var meetingId = (await service.CreateMeetingAsync(group.Id, actor, save, Ct)).Value;
        Func<Task<WorkgroupMutationResult>> act = async () =>
        {
            if (editing)
                return await service.UpdateMeetingAsync(meetingId, actor, save with { LocationUrl = url + "x" }, Ct);
            else
                return await service.CreateMeetingAsync(group.Id, actor, save with { LocationUrl = url + "x" }, Ct);
        };

        var refusal = (await act()).Refusal;
        refusal.Should().NotBeNull();
        refusal.Key.Should().Be(WorkgroupErrorKeys.TextTooLong);
        refusal.Args.Should().Equal(2000);
        await using var ctx = OpenContext();
        var meetings = await ctx.Meetings.Where(m => m.WorkgroupId == group.Id).ToListAsync(Ct);
        meetings.Should().ContainSingle().Which.LocationUrl.Should().Be(url);
    }
}
