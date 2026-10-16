using AwesomeAssertions;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Models;
using Humans.Workgroups.Services;
using Humans.Workgroups.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Xunit;

namespace Humans.Workgroups.Tests.Services;

public sealed class WorkgroupInputValidationTests : WorkgroupsTestHarness
{
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
        refusal!.Key.Should().Be(WorkgroupErrorKeys.TextTooLong);
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
        refusal!.Key.Should().Be(WorkgroupErrorKeys.TextTooLong);
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
        refusal!.Key.Should().Be(WorkgroupErrorKeys.TextTooLong);
        refusal.Args.Should().Equal(2000);
        await using var ctx = OpenContext();
        var meetings = await ctx.Meetings.Where(m => m.WorkgroupId == group.Id).ToListAsync(Ct);
        meetings.Should().ContainSingle().Which.LocationUrl.Should().Be(url);
    }
}
