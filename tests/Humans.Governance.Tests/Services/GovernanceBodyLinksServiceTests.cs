using AwesomeAssertions;
using Humans.Audit.Contracts;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Google.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using Humans.Governance.Services;
using Humans.Teams.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Governance.Tests.Services;

public sealed class GovernanceBodyLinksServiceTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 11, 19, 12, 0);

    private readonly ITeamService _teams = Substitute.For<ITeamService>();
    private readonly ITeamResourceService _resources = Substitute.For<ITeamResourceService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly IGoogleGroupSync _groupSync = Substitute.For<IGoogleGroupSync>();
    private readonly IGoogleSyncWrite _driveSync = Substitute.For<IGoogleSyncWrite>();
    private readonly Dictionary<Guid, IReadOnlyList<GoogleResourceSnapshot>> _resourcesByTeam = [];
    private readonly GovernanceBodyLinksService _sut;

    public GovernanceBodyLinksServiceTests()
    {
        var factory = new TestDbContextFactory<GovernanceDbContext>(
            TestDbOptions.InMemory<GovernanceDbContext>());
        _resources.GetResourcesByTeamIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(_ => _resourcesByTeam);
        SetPrefix(SystemTeamIds.Asociados, "asociados");
        SetPrefix(SystemTeamIds.Colaboradors, null);
        SetPrefix(SystemTeamIds.Board, "board");
        var repository = new GovernanceBodyRepository(factory);
        _sut = new GovernanceBodyLinksService(repository, _teams, _resources, _audit,
            new GovernanceBodySync(repository, _groupSync, _driveSync), new FakeClock(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void SetPrefix(Guid teamId, string? prefix) =>
        _teams.GetTeamAsync(teamId, Arg.Any<CancellationToken>()).Returns(new TeamInfo(
            teamId, "Team", null, "team", true, true, SystemTeamType.None, false, false, false, false, Now, [],
            GoogleGroupPrefix: prefix));

    private static GoogleResourceSnapshot Resource(Guid teamId, string googleId, GoogleResourceType type,
        DrivePermissionLevel level = DrivePermissionLevel.Contributor) =>
        new(Guid.NewGuid(), teamId, googleId, $"Name {googleId}", type, $"https://drive/{googleId}",
            DrivePermissionLevel: level);

    [HumansFact]
    public async Task Copy_takes_the_group_prefix_and_every_non_group_resource()
    {
        _resourcesByTeam[SystemTeamIds.Board] =
        [
            Resource(SystemTeamIds.Board, "folder", GoogleResourceType.DriveFolder, DrivePermissionLevel.ContentManager),
            Resource(SystemTeamIds.Board, "file", GoogleResourceType.DriveFile),
            Resource(SystemTeamIds.Board, "drive", GoogleResourceType.SharedDrive),
            Resource(SystemTeamIds.Board, "group", GoogleResourceType.Group),
        ];

        (await _sut.GetPendingCopiesAsync(Ct)).Should().HaveCount(3, "every body counts as pending until it has a row");

        (await _sut.CopyFromTeamsAsync(Guid.NewGuid(), Ct)).Should().Be(3);

        var stored = (await _sut.GetAllAsync(Ct)).ToDictionary(b => b.Body);
        stored[GovernanceBody.Board].GoogleGroupPrefix.Should().Be("board");
        stored[GovernanceBody.Board].Files.Select(f => f.GoogleId).Should().BeEquivalentTo(["folder", "file", "drive"]);
        stored[GovernanceBody.Board].Files.Single(f => string.Equals(f.GoogleId, "folder", StringComparison.Ordinal)).PermissionLevel
            .Should().Be(DrivePermissionLevel.ContentManager);
        stored[GovernanceBody.Colaboradores].GoogleGroupPrefix.Should().BeNull();
        stored[GovernanceBody.Colaboradores].Files.Should().BeEmpty("a body with nothing to copy still gets its row");
        (await _sut.GetPendingCopiesAsync(Ct)).Should().BeEmpty();
        await _audit.Received(3).LogAsync(
            GovernanceAuditActions.GovernanceBodyLinksCopied, Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task A_later_change_on_the_team_never_overwrites_the_stored_copy()
    {
        _resourcesByTeam[SystemTeamIds.Asociados] = [Resource(SystemTeamIds.Asociados, "old", GoogleResourceType.DriveFolder)];
        await _sut.CopyFromTeamsAsync(Guid.NewGuid(), Ct);

        _resourcesByTeam[SystemTeamIds.Asociados] = [Resource(SystemTeamIds.Asociados, "new", GoogleResourceType.DriveFolder)];
        SetPrefix(SystemTeamIds.Asociados, "asociados-2");

        (await _sut.GetPendingCopiesAsync(Ct)).Should().BeEmpty();
        (await _sut.CopyFromTeamsAsync(Guid.NewGuid(), Ct)).Should().Be(0);
        var asociados = (await _sut.GetAllAsync(Ct)).Single(b => b.Body == GovernanceBody.Asociados);
        asociados.GoogleGroupPrefix.Should().Be("asociados");
        asociados.Files.Select(f => f.GoogleId).Should().Equal("old");
    }

    [HumansFact]
    public async Task A_missing_team_leaves_the_stored_copy_alone()
    {
        await _sut.CopyFromTeamsAsync(Guid.NewGuid(), Ct);
        _teams.GetTeamAsync(SystemTeamIds.Board, Arg.Any<CancellationToken>()).Returns((TeamInfo?)null);

        (await _sut.GetPendingCopiesAsync(Ct)).Should().BeEmpty();
        (await _sut.CopyFromTeamsAsync(Guid.NewGuid(), Ct)).Should().Be(0);
        (await _sut.GetAllAsync(Ct)).Single(b => b.Body == GovernanceBody.Board).GoogleGroupPrefix.Should().Be("board");
    }

    [HumansFact]
    public async Task The_group_takes_a_full_address_and_refuses_a_bad_or_taken_name()
    {
        (await _sut.SetGroupAsync(GovernanceBody.Colaboradores, " Colaboradores@nobodies.team ", Guid.NewGuid(), Ct)).Success.Should().BeTrue();
        (await _sut.SetGroupAsync(GovernanceBody.Board, "colaboradores", Guid.NewGuid(), Ct)).ErrorKey
            .Should().Be(GovernanceBodyLinksService.GroupNameTaken);
        (await _sut.SetGroupAsync(GovernanceBody.Board, "board@gmail.com", Guid.NewGuid(), Ct)).ErrorKey
            .Should().Be(GovernanceBodyLinksService.InvalidGroupName);

        (await _sut.GetAllAsync(Ct)).Single(b => b.Body == GovernanceBody.Colaboradores).GoogleGroupPrefix.Should().Be("colaboradores");
        await _groupSync.Received(1).RequestSyncAsync("colaboradores@nobodies.team", Arg.Any<CancellationToken>(), GoogleSyncSource.SystemTeamSync);
        await _audit.Received(1).LogAsync(GovernanceAuditActions.GovernanceBodyLinksChanged, Arg.Any<string>(), SystemTeamIds.Colaboradors,
            "Colaboradores group set to colaboradores@nobodies.team", Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task A_file_is_added_once_Google_finds_it_and_can_be_removed()
    {
        _resources.LookUpDriveLinkAsync("https://drive/ok", Arg.Any<CancellationToken>()).Returns(new DriveLinkLookup(
            new DriveLinkItem("folder-1", GoogleResourceType.DriveFolder, "Shared/Board", "https://drive/folder-1")));
        _resources.LookUpDriveLinkAsync("https://drive/no", Arg.Any<CancellationToken>()).Returns(new DriveLinkLookup(null, "Not found."));

        (await _sut.AddFileAsync(GovernanceBody.Board, "https://drive/no", DrivePermissionLevel.Viewer, Guid.NewGuid(), Ct)).GoogleMessage
            .Should().Be("Not found.");
        (await _sut.AddFileAsync(GovernanceBody.Board, "https://drive/ok", DrivePermissionLevel.Viewer, Guid.NewGuid(), Ct)).Success
            .Should().BeTrue();
        (await _sut.AddFileAsync(GovernanceBody.Board, "https://drive/ok", DrivePermissionLevel.Viewer, Guid.NewGuid(), Ct)).ErrorKey
            .Should().Be(GovernanceBodyLinksService.FileAlreadyLinked);

        var file = (await _sut.GetAllAsync(Ct)).Single(b => b.Body == GovernanceBody.Board).Files.Single();
        (file.GoogleId, file.Name, file.PermissionLevel).Should().Be(("folder-1", "Shared/Board", DrivePermissionLevel.Viewer));
        await _driveSync.Received(1).RequestSyncAsync("folder-1", Arg.Any<CancellationToken>());

        (await _sut.RemoveFileAsync(GovernanceBody.Board, file.Id, Guid.NewGuid(), Ct)).Success.Should().BeTrue();
        (await _sut.GetAllAsync(Ct)).Single(b => b.Body == GovernanceBody.Board).Files.Should().BeEmpty();
        await _audit.Received(2).LogAsync(GovernanceAuditActions.GovernanceBodyLinksChanged, Arg.Any<string>(), SystemTeamIds.Board,
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task Edits_keep_the_files_a_body_already_has()
    {
        _resourcesByTeam[SystemTeamIds.Board] = [Resource(SystemTeamIds.Board, "kept", GoogleResourceType.DriveFolder)];
        await _sut.CopyFromTeamsAsync(Guid.NewGuid(), Ct);
        _resources.LookUpDriveLinkAsync("https://drive/new", Arg.Any<CancellationToken>()).Returns(new DriveLinkLookup(
            new DriveLinkItem("new", GoogleResourceType.DriveFile, "New", null)));

        (await _sut.AddFileAsync(GovernanceBody.Board, "https://drive/new", DrivePermissionLevel.Viewer, Guid.NewGuid(), Ct)).Success.Should().BeTrue();
        (await _sut.SetGroupAsync(GovernanceBody.Board, "board-2", Guid.NewGuid(), Ct)).Success.Should().BeTrue();

        var board = (await _sut.GetAllAsync(Ct)).Single(b => b.Body == GovernanceBody.Board);
        board.GoogleGroupPrefix.Should().Be("board-2");
        board.Files.Select(f => f.GoogleId).Should().BeEquivalentTo(["kept", "new"]);
    }
}
