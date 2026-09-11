using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;
using Humans.Testing;
using Humans.Users.Contracts;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Services.Contributors;
using Humans.Workgroups.Tests.Infrastructure;
using NodaTime;
using NSubstitute;

namespace Humans.Workgroups.Tests.Services.Contributors;

/// <summary>
/// Workgroups' half of the Drive access fan-out (design §9, §20): Active folders go
/// Contributor for current members, Dormant folders go Viewer, and the configured root
/// goes Viewer for the Board and approved Colaboradors/Asociados. A group with no folder
/// is not claimed, and a <c>folderId</c> filter returns at most that one entry.
/// </summary>
public sealed class WorkgroupDriveAccessSourceTests : WorkgroupsTestHarness
{
    private WorkgroupDriveAccessSource NewSource() => new(NewService(), Users, Teams);

    [HumansFact]
    public async Task ActiveGroup_CurrentMembers_GetContributor()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Active, driveFolderId: "folder-1");
        var coordinator = workgroup.Members.Single().UserId;

        var access = await NewSource().GetExpectedAccessAsync(ct: Ct);

        access.Should().ContainKey("folder-1");
        access["folder-1"].Should().ContainKey(coordinator).WhoseValue.Should().Be(DrivePermissionLevel.Contributor);
    }

    [HumansFact]
    public async Task DormantGroup_CurrentMembers_GetViewer()
    {
        var workgroup = await SeedWorkgroupAsync(
            status: WorkgroupStatus.Dormant, dormantReason: WorkgroupDormantReason.Quiet, driveFolderId: "folder-2");
        var coordinator = workgroup.Members.Single().UserId;

        var access = await NewSource().GetExpectedAccessAsync(ct: Ct);

        access["folder-2"][coordinator].Should().Be(DrivePermissionLevel.Viewer);
    }

    [HumansFact]
    public async Task AGroupWithNoDriveFolder_IsNotClaimed()
    {
        await SeedWorkgroupAsync(status: WorkgroupStatus.Applied, driveFolderId: null);

        var access = await NewSource().GetExpectedAccessAsync(ct: Ct);

        // The configured root folder is always claimed; a group with no folder of its own adds nothing.
        access.Keys.Should().Equal("root-folder");
    }

    [HumansFact]
    public async Task RootFolder_BoardMembers_AndApprovedColaboradorsAndAsociados_GetViewer()
    {
        var boardUserId = Guid.NewGuid();
        Teams.GetTeamAsync(SystemTeamIds.Board, Arg.Any<CancellationToken>())
            .Returns(new TeamInfo(
                SystemTeamIds.Board, "Board", null, "board", true, true,
                SystemTeamType.Board, false, false, false, false, Instant.FromUtc(2026, 1, 1, 0, 0),
                [new TeamMemberInfo(Guid.NewGuid(), boardUserId, "Board Member", null, null, TeamMemberRole.Member,
                    Instant.FromUtc(2026, 1, 1, 0, 0))]));

        var colaborador = SeedUser("Colaborador", profile: UserFixtures.Profile(
            isApproved: true, membershipTier: MembershipTier.Colaborador));
        var asociado = SeedUser("Asociado", profile: UserFixtures.Profile(
            isApproved: true, membershipTier: MembershipTier.Asociado));
        var unapprovedColaborador = SeedUser("Unapproved", profile: UserFixtures.Profile(
            isApproved: false, membershipTier: MembershipTier.Colaborador));
        var volunteer = SeedUser("Volunteer", profile: UserFixtures.Profile(
            isApproved: true, membershipTier: MembershipTier.Volunteer));

        var access = await NewSource().GetExpectedAccessAsync(ct: Ct);

        access.Should().ContainKey("root-folder");
        var readers = access["root-folder"];
        readers[boardUserId].Should().Be(DrivePermissionLevel.Viewer);
        readers[colaborador].Should().Be(DrivePermissionLevel.Viewer);
        readers[asociado].Should().Be(DrivePermissionLevel.Viewer);
        readers.Should().NotContainKey(unapprovedColaborador);
        readers.Should().NotContainKey(volunteer);
    }

    [HumansFact]
    public async Task FolderIdFilter_ReturnsAtMostThatOneEntry()
    {
        await SeedWorkgroupAsync(status: WorkgroupStatus.Active, driveFolderId: "folder-a", name: "A");
        await SeedWorkgroupAsync(status: WorkgroupStatus.Active, driveFolderId: "folder-b", name: "B");

        var access = await NewSource().GetExpectedAccessAsync("folder-a", Ct);

        access.Keys.Should().ContainSingle().Which.Should().Be("folder-a");
    }
}
