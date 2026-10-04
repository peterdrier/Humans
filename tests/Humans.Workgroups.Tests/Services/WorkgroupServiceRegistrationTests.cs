using AwesomeAssertions;
using Humans.Finance.Contracts;
using Humans.Settings.Contracts;
using Humans.Base.Constants;
using Humans.Users.Contracts;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Services;
using Humans.Workgroups.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Humans.Workgroups.Tests.Services;

/// <summary>
/// Registration's two failure modes (design §6, §20): a Drive failure and an unset root
/// folder both leave the group exactly as it was — Applied, no folder id.
/// </summary>
public sealed class WorkgroupServiceRegistrationTests : WorkgroupsTestHarness
{
    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Registration_CompletesWhenTheRequestDisconnectsDuringFolderCreation(bool existing)
    {
        using var request = new CancellationTokenSource();
        var actor = SeedUser();
        var group = await SeedWorkgroupAsync(status: WorkgroupStatus.Applied, driveFolderId: null);
        GoogleSync.CreateSubfolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<CancellationToken>().CanBeCanceled.Should().BeFalse();
                request.Cancel();
                return "created-folder";
            });

        var service = NewService();
        var id = group.Id;
        if (existing)
            id = await service.RegisterExistingAsync(actor, new WorkgroupBootstrap(
                new WorkgroupApplication("Existing", "Purpose", "Report", WorkgroupDeliverableKind.Report,
                    WorkgroupAudience.Board, null, null, null), actor, Clock.GetCurrentInstant()), request.Token);
        else
            await service.RegisterAsync(id, actor, request.Token);

        await using var ctx = OpenContext();
        var saved = await ctx.Workgroups.SingleAsync(w => w.Id == id, Ct);
        saved.Status.Should().Be(WorkgroupStatus.Active);
        saved.DriveFolderId.Should().Be("created-folder");
        (await ctx.LogEntries.AnyAsync(e => e.WorkgroupId == id && e.Kind == WorkgroupLogKind.Registered, Ct))
            .Should().BeTrue();
        await GoogleSync.Received().RequestSyncAsync("created-folder", CancellationToken.None);
    }

    [HumansFact]
    public async Task Register_WhenSubfolderCreationFails_LeavesTheGroupApplied()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Applied, driveFolderId: null);
        GoogleSync.CreateSubfolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Drive is down"));

        var act = () => NewService().RegisterAsync(workgroup.Id, SeedUser(), Ct);

        var error = (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which;
        error.Key.Should().Be(WorkgroupErrorKeys.DriveFolderCreationFailed);
        error.Message.Should().Be("Workgroup rule 'Workgroups_Error_DriveFolderCreationFailed' rejected the operation.");

        await using var ctx = OpenContext();
        var reloaded = await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct);
        reloaded.Status.Should().Be(WorkgroupStatus.Applied);
        reloaded.DriveFolderId.Should().BeNull();
        reloaded.RegisteredAt.Should().BeNull();
    }

    [HumansFact]
    public async Task Register_WhenTheRootFolderIsUnset_ThrowsAndLeavesTheGroupApplied()
    {
        RootFolderId = null;
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Applied, driveFolderId: null);

        var act = () => NewService().RegisterAsync(workgroup.Id, SeedUser(), Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.RootFolderNotConfigured);

        await using var ctx = OpenContext();
        var reloaded = await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct);
        reloaded.Status.Should().Be(WorkgroupStatus.Applied);
        reloaded.DriveFolderId.Should().BeNull();
        await GoogleSync.DidNotReceive().CreateSubfolderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SetRootDriveFolderId_Blank_Throws()
    {
        var act = () => NewService().SetRootDriveFolderIdAsync(" ", SeedUser(), Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.RootFolderNotConfigured);
    }

    [HumansFact]
    public async Task SetRootDriveFolderId_Valid_PersistsThroughSettings()
    {
        await NewService().SetRootDriveFolderIdAsync("new-root", SeedUser(), Ct);

        await Settings.Received(1).SetValueAsync(
            SettingKeys.WorkgroupsRootDriveFolderId, "new-root", Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData("role")]
    [Xunit.InlineData("address")]
    [Xunit.InlineData("member")]
    public async Task Apply_EmailRecipientLookupFailureDoesNotFailTheSavedApplication(string failure)
    {
        var boardMember = SeedUser();
        Roles.GetActiveUserIdsInRoleAsync(RoleNames.Board, Arg.Any<CancellationToken>())
            .Returns([boardMember]);
        var unavailable = new InvalidOperationException("Email recipient lookup unavailable");
        if (string.Equals(failure, "role", StringComparison.Ordinal))
            Roles.GetActiveUserIdsInRoleAsync(RoleNames.Board, Arg.Any<CancellationToken>())
                .ThrowsAsync(unavailable);
        else if (string.Equals(failure, "address", StringComparison.Ordinal))
            UserEmails.GetNotificationTargetEmailsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(unavailable);
        else
            Users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
                .Returns(ValueTask.FromException<IReadOnlyDictionary<Guid, UserInfo>>(unavailable));

        var id = await NewService().ApplyAsync(SeedUser(), new WorkgroupApplication(
            "New group", "Purpose", "A report", WorkgroupDeliverableKind.Report,
            WorkgroupAudience.Board, null, null, null), Ct);

        await using var ctx = OpenContext();
        (await ctx.Workgroups.SingleAsync(w => w.Id == id, Ct)).Status.Should().Be(WorkgroupStatus.Applied);
    }

    [HumansFact]
    public async Task Register_EmailAddressLookupFailureStillRequestsDriveAccessSync()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Applied, driveFolderId: null);
        UserEmails.GetNotificationTargetEmailsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Email recipient lookup unavailable"));

        await NewService().RegisterAsync(workgroup.Id, SeedUser(), Ct);

        await using var ctx = OpenContext();
        var saved = await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct);
        saved.Status.Should().Be(WorkgroupStatus.Active);
        await GoogleSync.Received().RequestSyncAsync(saved.DriveFolderId!, CancellationToken.None);
    }

    [HumansFact]
    public async Task RegisterExisting_WithBudget_BindsTheAccount()
    {
        var coordinator = SeedUser("Coordinator");
        var bootstrap = new WorkgroupBootstrap(
            new WorkgroupApplication("ALM 2027", "Purpose", "A report", WorkgroupDeliverableKind.Report,
                WorkgroupAudience.Board, null, null, null),
            coordinator, Clock.GetCurrentInstant(),
            new WorkgroupBudgetSave(1200m, null));

        var id = await NewService().RegisterExistingAsync(SeedUser("Secretary"), bootstrap, Ct);

        await using var ctx = OpenContext();
        var w = await ctx.Workgroups.SingleAsync(x => x.Id == id, Ct);
        w.BudgetAmount.Should().Be(1200m);
        w.HoldedAccountNumber.Should().Be(62900150);
    }

    [HumansFact]
    public async Task RegisterExisting_FinanceFails_RegistersNothing()
    {
        Finance.CreateOrLinkExpenseAccountAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns<HoldedExpenseAccountRef>(_ => throw new InvalidOperationException("Holded is down."));
        var bootstrap = new WorkgroupBootstrap(
            new WorkgroupApplication("ALM 2027", "Purpose", "A report", WorkgroupDeliverableKind.Report,
                WorkgroupAudience.Board, null, null, null),
            SeedUser("Coordinator"), Clock.GetCurrentInstant(),
            new WorkgroupBudgetSave(1200m, null));

        var act = () => NewService().RegisterExistingAsync(SeedUser("Secretary"), bootstrap, Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.BudgetAccountFailed);
        await using var ctx = OpenContext();
        (await ctx.Workgroups.AnyAsync(Ct)).Should().BeFalse();
        await GoogleSync.DidNotReceive().CreateSubfolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Apply_WithAVeryLongName_TrimsTheSlugToItsColumn()
    {
        var name = new string('a', 200);

        var id = await NewService().ApplyAsync(SeedUser(), new WorkgroupApplication(
            name, "Purpose", "A report", WorkgroupDeliverableKind.Report,
            WorkgroupAudience.Board, TargetDate: null, DiscordChannelUrl: null,
            SecondCoordinatorUserId: null), Ct);

        await using var ctx = OpenContext();
        var saved = await ctx.Workgroups.SingleAsync(w => w.Id == id, Ct);
        saved.Slug.Length.Should().BeLessThanOrEqualTo(96);
    }
}
