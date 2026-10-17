using Xunit;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
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
    [Xunit.InlineData("apply-second", false)]
    [Xunit.InlineData("apply-second", true)]
    [Xunit.InlineData("existing-primary", false)]
    [Xunit.InlineData("existing-primary", true)]
    [Xunit.InlineData("existing-second", false)]
    [Xunit.InlineData("existing-second", true)]
    public async Task Registration_InvalidCoordinator_RefusesBeforeWrites(string path, bool terminal)
    {
        var actor = SeedUser();
        var target = Guid.NewGuid();
        if (terminal)
        {
            var person = (await Users.GetUserInfoAsync(actor, Ct))! with
            { Id = target, State = UserState.Deleted, MergedAt = Clock.GetCurrentInstant() };
            Users.GetUserInfoAsync(target, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(person));
        }
        var application = new WorkgroupApplication("People check", "Purpose", "Report", WorkgroupDeliverableKind.Report,
            WorkgroupAudience.Board, null, null, string.Equals(path, "existing-primary", StringComparison.Ordinal) ? null : target);
        var service = NewService();
        var result = string.Equals(path, "apply-second", StringComparison.Ordinal)
            ? await service.ApplyAsync(actor, application, Ct)
            : await service.RegisterExistingAsync(actor, new WorkgroupBootstrap(application,
                string.Equals(path, "existing-primary", StringComparison.Ordinal) ? target : actor,
                Clock.GetCurrentInstant(), new WorkgroupBudgetSave(100m, null)), Ct);

        result.Refusal!.Key.Should().Be(WorkgroupErrorKeys.NotFound);
        await using var ctx = OpenContext();
        (await ctx.Workgroups.CountAsync(Ct)).Should().Be(0);
        (await ctx.Members.CountAsync(Ct)).Should().Be(0);
        AuditLog.ReceivedCalls().Should().BeEmpty();
        await GoogleSync.DidNotReceive().CreateSubfolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await Finance.DidNotReceive().CreateOrLinkExpenseAccountAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData("apply-second")]
    [Xunit.InlineData("apply-self-alias")]
    [Xunit.InlineData("existing-primary")]
    [Xunit.InlineData("existing-second")]
    public async Task Registration_MergeAlias_UsesCanonicalCoordinator(string path)
    {
        var actor = SeedUser();
        var survivor = string.Equals(path, "apply-self-alias", StringComparison.Ordinal) ? actor : SeedUser("Survivor");
        var source = Guid.NewGuid();
        var survivorInfo = await Users.GetUserInfoAsync(survivor, Ct);
        Users.GetUserInfoAsync(source, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(survivorInfo));
        var primary = string.Equals(path, "existing-primary", StringComparison.Ordinal);
        var application = new WorkgroupApplication("People check", "Purpose", "Report", WorkgroupDeliverableKind.Report,
            WorkgroupAudience.Board, null, null, primary ? null : source);
        var result = path.StartsWith("apply", StringComparison.Ordinal)
            ? await NewService().ApplyAsync(actor, application, Ct)
            : await NewService().RegisterExistingAsync(actor, new WorkgroupBootstrap(application,
                primary ? source : actor, Clock.GetCurrentInstant()), Ct);

        result.Refusal.Should().BeNull();
        await using var ctx = OpenContext();
        var members = await ctx.Members.AsNoTracking().ToListAsync(Ct);
        members.Select(m => m.UserId).Should().Contain(survivor).And.NotContain(source);
        members.Count.Should().Be(primary || survivor == actor ? 1 : 2);
        (await ctx.Workgroups.SingleAsync(Ct)).AppliedByUserId.Should().Be(primary ? survivor : actor);
    }

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
                call.ArgAt<string>(0).Should().Be("section-owned-root");
                call.Arg<CancellationToken>().CanBeCanceled.Should().BeFalse();
                request.Cancel();
                return "created-folder";
            });

        var service = NewService();
        await service.SetRootDriveFolderIdAsync("section-owned-root", actor, Ct);
        var id = group.Id;
        if (existing)
            id = (await service.RegisterExistingAsync(actor, new WorkgroupBootstrap(
                new WorkgroupApplication("Existing", "Purpose", "Report", WorkgroupDeliverableKind.Report,
                    WorkgroupAudience.Board, null, null, null), actor, Clock.GetCurrentInstant()), request.Token)).Value;
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

        (await act()).Refusal!.Key
            .Should().Be(WorkgroupErrorKeys.DriveFolderCreationFailed);

        await using var ctx = OpenContext();
        var reloaded = await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct);
        reloaded.Status.Should().Be(WorkgroupStatus.Applied);
        reloaded.DriveFolderId.Should().BeNull();
        reloaded.RegisteredAt.Should().BeNull();
    }

    [HumansFact]
    public async Task Register_WhenTheRootFolderIsUnset_RefusesAndLeavesTheGroupApplied()
    {
        RootFolderId = null;
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Applied, driveFolderId: null);

        var act = () => NewService().RegisterAsync(workgroup.Id, SeedUser(), Ct);

        (await act()).Refusal!.Key
            .Should().Be(WorkgroupErrorKeys.RootFolderNotConfigured);

        await using var ctx = OpenContext();
        var reloaded = await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct);
        reloaded.Status.Should().Be(WorkgroupStatus.Applied);
        reloaded.DriveFolderId.Should().BeNull();
        await GoogleSync.DidNotReceive().CreateSubfolderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SetRootDriveFolderId_Blank_ReturnsARefusal()
    {
        var act = () => NewService().SetRootDriveFolderIdAsync(" ", SeedUser(), Ct);

        (await act()).Refusal!.Key
            .Should().Be(WorkgroupErrorKeys.RootFolderNotConfigured);
    }

    [HumansFact]
    public async Task SetRootDriveFolderId_Unreachable_RefusesAndKeepsTheOldValue()
    {
        GoogleSync.IsFolderReachableAsync("gone-root", Arg.Any<CancellationToken>()).Returns(false);
        var service = NewService();

        var result = await service.SetRootDriveFolderIdAsync("https://drive.google.com/drive/folders/gone-root", SeedUser(), Ct);

        result.Refusal!.Key.Should().Be(WorkgroupErrorKeys.RootFolderUnreachable);
        (await service.GetRootDriveFolderIdAsync(Ct)).Should().Be(RootFolderId);
        AuditLog.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task RootDriveFolder_PreservesLegacyValueUntilAdminSavesSectionOwnedSetting()
    {
        var actor = SeedUser();
        var service = NewService();
        (await service.GetRootDriveFolderIdAsync(Ct)).Should().Be(RootFolderId);
        (await Db.Settings.CountAsync(Ct)).Should().Be(0, "reads must not backfill state");
        Settings.ClearReceivedCalls();

        await service.SetRootDriveFolderIdAsync(" new-root ", actor, Ct);
        RootFolderId = "stale-legacy-root";
        (await NewService().GetRootDriveFolderIdAsync(Ct)).Should().Be("new-root");
        await service.SetRootDriveFolderIdAsync("another-root", actor, Ct);
        (await NewService().GetRootDriveFolderIdAsync(Ct)).Should().Be("another-root");
        (await Db.Settings.AsNoTracking().SingleAsync(Ct)).RootDriveFolderId.Should().Be("another-root");
        Settings.ReceivedCalls().Should().BeEmpty("saved section state must not read or write the legacy key");
        await AuditLog.Received(1).LogAsync(AuditAction.WorkgroupsRootFolderUpdated,
            AuditEntityTypes.WorkgroupsSettings, Guid.Empty, "Root Drive folder set to new-root", actor);
    }

    [HumansTheory]
    [Xunit.InlineData("https://drive.google.com/drive/folders/0AFLHTK5u0wymUk9PVA")]
    [Xunit.InlineData("https://drive.google.com/drive/u/1/folders/0AFLHTK5u0wymUk9PVA?usp=sharing")]
    [Xunit.InlineData("https://drive.google.com/open?id=0AFLHTK5u0wymUk9PVA")]
    [Xunit.InlineData(" 0AFLHTK5u0wymUk9PVA ")]
    public async Task SetRootDriveFolderId_PastedUrl_StoresBareId(string input)
    {
        await NewService().SetRootDriveFolderIdAsync(input, SeedUser(), Ct);

        (await Db.Settings.AsNoTracking().SingleAsync(Ct)).RootDriveFolderId.Should().Be("0AFLHTK5u0wymUk9PVA");
    }

    [HumansFact]
    public async Task GetRootDriveFolderId_StoredUrl_ReturnsBareId()
    {
        RootFolderId = "https://drive.google.com/drive/folders/0AFLHTK5u0wymUk9PVA";

        (await NewService().GetRootDriveFolderIdAsync(Ct)).Should().Be("0AFLHTK5u0wymUk9PVA");
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

        var id = (await NewService().ApplyAsync(SeedUser(), new WorkgroupApplication(
            "New group", "Purpose", "A report", WorkgroupDeliverableKind.Report,
            WorkgroupAudience.Board, null, null, null), Ct)).Value;

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

        var id = (await NewService().RegisterExistingAsync(SeedUser("Secretary"), bootstrap, Ct)).Value;

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

        (await act()).Refusal!.Key
            .Should().Be(WorkgroupErrorKeys.BudgetAccountFailed);
        await using var ctx = OpenContext();
        (await ctx.Workgroups.AnyAsync(Ct)).Should().BeFalse();
        await GoogleSync.DidNotReceive().CreateSubfolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData("apply", "研究组")]
    [InlineData("existing", "é")]
    [InlineData("rename", "🛠️")]
    public async Task NamesWithoutAsciiSlugCharacters_KeepTheirNameAndReserveAFallback(string path, string name)
    {
        await SeedWorkgroupAsync(name: "Workgroup"); // Occupies the fallback seed.
        var actor = SeedUser("Coordinator");
        var service = NewService();
        Guid id;
        if (string.Equals(path, "rename", StringComparison.Ordinal))
        {
            var original = await SeedWorkgroupAsync(name: "Original", coordinatorUserId: actor);
            var result = await service.EditRegisterAsync(original.Id, actor,
                new WorkgroupRegisterEdit(name, "Purpose", "A report", WorkgroupDeliverableKind.Report,
                    WorkgroupAudience.Board, null, null), Ct);
            result.Refusal.Should().BeNull();
            id = original.Id;
        }
        else
        {
            var application = new WorkgroupApplication(name, "Purpose", "A report", WorkgroupDeliverableKind.Report,
                WorkgroupAudience.Board, null, null, null);
            var result = string.Equals(path, "apply", StringComparison.Ordinal)
                ? await service.ApplyAsync(actor, application, Ct)
                : await service.RegisterExistingAsync(actor, new WorkgroupBootstrap(application, actor,
                    Clock.GetCurrentInstant(), new WorkgroupBudgetSave(100m, null)), Ct);
            result.Refusal.Should().BeNull();
            id = result.Value;
        }

        await using var ctx = OpenContext();
        var saved = await ctx.Workgroups.SingleAsync(w => w.Id == id, Ct);
        saved.Name.Should().Be(name);
        saved.Slug.Should().Be("workgroup-2");
    }

    [HumansFact]
    public async Task Apply_WithAVeryLongName_TrimsTheSlugToItsColumn()
    {
        var name = new string('a', 200);

        var id = (await NewService().ApplyAsync(SeedUser(), new WorkgroupApplication(
            name, "Purpose", "A report", WorkgroupDeliverableKind.Report,
            WorkgroupAudience.Board, TargetDate: null, DiscordChannelUrl: null,
            SecondCoordinatorUserId: null), Ct)).Value;

        await using var ctx = OpenContext();
        var saved = await ctx.Workgroups.SingleAsync(w => w.Id == id, Ct);
        saved.Slug.Length.Should().BeLessThanOrEqualTo(96);
    }

    [HumansTheory]
    [InlineData("Admin", "admin-2")]
    [InlineData("Apply", "apply-2")]
    public async Task Apply_WithAReservedRouteName_GetsASuffixedSlug(string name, string expectedSlug)
    {
        var id = (await NewService().ApplyAsync(SeedUser(), new WorkgroupApplication(
            name, "Purpose", "A report", WorkgroupDeliverableKind.Report,
            WorkgroupAudience.Board, TargetDate: null, DiscordChannelUrl: null,
            SecondCoordinatorUserId: null), Ct)).Value;

        await using var ctx = OpenContext();
        (await ctx.Workgroups.SingleAsync(w => w.Id == id, Ct)).Slug.Should().Be(expectedSlug);
    }
}
