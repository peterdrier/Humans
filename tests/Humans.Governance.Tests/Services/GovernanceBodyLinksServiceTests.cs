using AwesomeAssertions;
using Humans.Audit.Contracts;
using Humans.Base.Enums;
using Humans.Google.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using Humans.Governance.Services;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Governance.Tests.Services;

public sealed class GovernanceBodyLinksServiceTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 11, 19, 12, 0);

    private readonly ITeamResourceService _resources = Substitute.For<ITeamResourceService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly IGoogleGroupSync _groupSync = Substitute.For<IGoogleGroupSync>();
    private readonly IGoogleSyncWrite _driveSync = Substitute.For<IGoogleSyncWrite>();
    private readonly GovernanceBodyLinksService _sut;

    public GovernanceBodyLinksServiceTests()
    {
        var factory = new TestDbContextFactory<GovernanceDbContext>(
            TestDbOptions.InMemory<GovernanceDbContext>());
        var repository = new GovernanceBodyRepository(factory);
        _sut = new GovernanceBodyLinksService(repository, _resources, _audit,
            new GovernanceBodySync(repository, _groupSync, _driveSync), new FakeClock(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
        await _audit.Received(1).LogAsync(GovernanceAuditActions.GovernanceBodyLinksChanged, Arg.Any<string>(), GovernanceBodyLinksService.AuditIds[GovernanceBody.Colaboradores],
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
        await _audit.Received(2).LogAsync(GovernanceAuditActions.GovernanceBodyLinksChanged, Arg.Any<string>(), GovernanceBodyLinksService.AuditIds[GovernanceBody.Board],
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task Edits_keep_the_files_a_body_already_has()
    {
        _resources.LookUpDriveLinkAsync("https://drive/kept", Arg.Any<CancellationToken>()).Returns(new DriveLinkLookup(
            new DriveLinkItem("kept", GoogleResourceType.DriveFolder, "Kept", null)));
        _resources.LookUpDriveLinkAsync("https://drive/new", Arg.Any<CancellationToken>()).Returns(new DriveLinkLookup(
            new DriveLinkItem("new", GoogleResourceType.DriveFile, "New", null)));
        await _sut.AddFileAsync(GovernanceBody.Board, "https://drive/kept", DrivePermissionLevel.Viewer, Guid.NewGuid(), Ct);

        (await _sut.AddFileAsync(GovernanceBody.Board, "https://drive/new", DrivePermissionLevel.Viewer, Guid.NewGuid(), Ct)).Success.Should().BeTrue();
        (await _sut.SetGroupAsync(GovernanceBody.Board, "board-2", Guid.NewGuid(), Ct)).Success.Should().BeTrue();

        var board = (await _sut.GetAllAsync(Ct)).Single(b => b.Body == GovernanceBody.Board);
        board.GoogleGroupPrefix.Should().Be("board-2");
        board.Files.Select(f => f.GoogleId).Should().BeEquivalentTo(["kept", "new"]);
    }
}
