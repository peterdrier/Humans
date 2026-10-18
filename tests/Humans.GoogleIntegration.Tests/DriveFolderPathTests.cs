using AwesomeAssertions;
using Humans.GoogleIntegration.Services;
using Humans.GoogleIntegration.Services.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.GoogleIntegration.Tests;

/// <summary>
/// A Shared Drive's root is the drive itself: its id is the drive id, Drive reads it as an anonymous
/// folder named "Drive" with no parents, and the real name exists only on drives.get.
/// </summary>
public sealed class DriveFolderPathTests
{
    private const string DriveId = "0AFakeSharedDriveId";
    private readonly IGoogleDrivePermissionsClient _drive = Substitute.For<IGoogleDrivePermissionsClient>();

    [HumansFact]
    public async Task ResolveAsync_DriveRootAsTarget_IsNamedByTheDrive()
    {
        StubFile(DriveId, "Drive", parents: null, driveId: DriveId);
        StubDriveName("Nobodies");

        (await ResolveAsync(DriveId)).Should().Be("Nobodies");
    }

    [HumansFact]
    public async Task ResolveAsync_DriveRootWhoseNameLookupFails_IsUnresolvedNotThePlaceholder()
    {
        StubFile(DriveId, "Drive", parents: null, driveId: DriveId);
        _drive.GetSharedDriveAsync(DriveId, Arg.Any<CancellationToken>())
            .Returns(new SharedDriveMetadataResult(null, new GoogleClientError(503, "unavailable")));

        (await ResolveAsync(DriveId)).Should().BeNull();
    }

    [HumansFact]
    public async Task ResolveAsync_FolderUnderDriveWhoseNameLookupFails_IsUnresolvedNotPrefixedWithThePlaceholder()
    {
        StubFile("child", "Finance", parents: [DriveId], driveId: DriveId);
        StubFile(DriveId, "Drive", parents: null, driveId: DriveId);
        _drive.GetSharedDriveAsync(DriveId, Arg.Any<CancellationToken>())
            .Returns(new SharedDriveMetadataResult(null, new GoogleClientError(503, "unavailable")));

        (await ResolveAsync("child")).Should().BeNull();
    }

    [HumansFact]
    public async Task ResolveAsync_DriveRootThatOmitsItsOwnDriveId_AsTarget_IsStillNamedByTheDrive()
    {
        StubFile(DriveId, "Drive", parents: null, driveId: null);
        StubDriveName("Nobodies");

        (await ResolveAsync(DriveId)).Should().Be("Nobodies");
    }

    [HumansFact]
    public async Task ResolveAsync_DriveRootThatOmitsItsOwnDriveId_UnderAFolder_IsRecognisedFromTheChild()
    {
        StubFile("child", "Finance", parents: [DriveId], driveId: DriveId);
        StubFile(DriveId, "Drive", parents: null, driveId: null);
        StubDriveName("Nobodies");

        (await ResolveAsync("child")).Should().Be("Nobodies / Finance");
    }

    [HumansFact]
    public async Task ResolveAsync_ParentlessFolderThatIsNoDrive_KeepsItsOwnName()
    {
        StubFile("orphan", "Lonely", parents: null, driveId: null);
        _drive.GetSharedDriveAsync("orphan", Arg.Any<CancellationToken>())
            .Returns(new SharedDriveMetadataResult(null, new GoogleClientError(404, "not a drive")));

        (await ResolveAsync("orphan")).Should().Be("Lonely");
    }

    [HumansTheory]
    [InlineData(503, "unavailable")]
    [InlineData(403, "rateLimitExceeded")]
    public async Task ResolveAsync_ParentlessFolderWhoseDriveProbeFailsTransiently_IsUnresolved(int status, string message)
    {
        StubFile("orphan", "Drive", parents: null, driveId: null);
        _drive.GetSharedDriveAsync("orphan", Arg.Any<CancellationToken>())
            .Returns(new SharedDriveMetadataResult(null, new GoogleClientError(status, message)));

        (await ResolveAsync("orphan")).Should().BeNull();
    }

    private Task<string?> ResolveAsync(string fileId) =>
        DriveFolderPath.ResolveAsync(_drive, fileId, NullLogger.Instance, Xunit.TestContext.Current.CancellationToken);

    private void StubFile(string id, string name, string[]? parents, string? driveId) =>
        _drive.GetFileAsync(id, Arg.Any<CancellationToken>())
            .Returns(new DriveFileMetadataResult(new DriveFileMetadata(id, name, parents, driveId, null), null));

    private void StubDriveName(string name) =>
        _drive.GetSharedDriveAsync(DriveId, Arg.Any<CancellationToken>())
            .Returns(new SharedDriveMetadataResult(new SharedDriveMetadata(DriveId, name), null));
}
