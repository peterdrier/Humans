using AwesomeAssertions;
using Humans.Settings.Contracts;
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

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key
            .Should().Be(WorkgroupErrorKeys.DriveFolderCreationFailed);

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
