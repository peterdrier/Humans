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
}
