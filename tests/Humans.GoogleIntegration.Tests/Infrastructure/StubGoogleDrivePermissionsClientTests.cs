using AwesomeAssertions;
using Humans.GoogleIntegration.Services.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace Humans.GoogleIntegration.Tests.Infrastructure;

/// <summary>
/// Contract tests for <see cref="StubGoogleDrivePermissionsClient"/>. These
/// exercise the idempotency and lifecycle contracts shared with the real
/// <see cref="GoogleDrivePermissionsClient"/>: permission adds deduplicate
/// by email, deletes target an
/// existing permission id, and the file metadata round-trips the
/// inherited-permissions-disabled flag.
/// </summary>
public class StubGoogleDrivePermissionsClientTests
{
    private readonly StubGoogleDrivePermissionsClient _client =
        new(NullLogger<StubGoogleDrivePermissionsClient>.Instance);

    [HumansFact]
    public async Task ListPermissionsAsync_EmptyFolder_ReturnsEmptyList()
    {
        var folderId = _client.SeedFolder("Team A");

        var result = await _client.ListPermissionsAsync(folderId, Xunit.TestContext.Current.CancellationToken);

        result.Error.Should().BeNull();
        result.Permissions.Should().NotBeNull().And.BeEmpty();
    }

    [HumansFact]
    public async Task CreatePermissionAsync_NewEmail_ReturnsCreated()
    {
        var folderId = _client.SeedFolder("Team A");

        var result = await _client.CreatePermissionAsync(
            folderId, "alice@nobodies.team", "writer", Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(DrivePermissionCreateOutcome.Created);
        result.Error.Should().BeNull();
    }

    [HumansFact]
    public async Task CreatePermissionAsync_DuplicateEmail_ReturnsAlreadyExists()
    {
        var folderId = _client.SeedFolder("Team A");
        await _client.CreatePermissionAsync(folderId, "alice@nobodies.team", "writer", Xunit.TestContext.Current.CancellationToken);

        var second = await _client.CreatePermissionAsync(
            folderId, "alice@nobodies.team", "reader", Xunit.TestContext.Current.CancellationToken);

        second.Outcome.Should().Be(DrivePermissionCreateOutcome.AlreadyExists,
            because: "the real client treats Google's 400 'already exists' as idempotent success");
    }

    [HumansFact]
    public async Task ListPermissionsAsync_AfterAdd_ContainsUserPermission()
    {
        var folderId = _client.SeedFolder("Team A");
        await _client.CreatePermissionAsync(folderId, "alice@nobodies.team", "writer", Xunit.TestContext.Current.CancellationToken);

        var result = await _client.ListPermissionsAsync(folderId, Xunit.TestContext.Current.CancellationToken);

        result.Permissions.Should().ContainSingle();
        var perm = result.Permissions!.Single();
        perm.Type.Should().Be("user");
        perm.Role.Should().Be("writer");
        perm.EmailAddress.Should().Be("alice@nobodies.team");
        perm.HasInheritedComponent.Should().BeFalse(
            because: "stub permissions are treated as direct — tests covering inherited-vs-direct filtering belong to the real-client integration tests");
    }

    [HumansFact]
    public async Task DeletePermissionAsync_Existing_RemovesIt()
    {
        var folderId = _client.SeedFolder("Team A");
        await _client.CreatePermissionAsync(folderId, "alice@nobodies.team", "writer", Xunit.TestContext.Current.CancellationToken);
        var before = await _client.ListPermissionsAsync(folderId, Xunit.TestContext.Current.CancellationToken);
        var permId = before.Permissions!.Single().Id!;

        var deleteResult = await _client.DeletePermissionAsync(folderId, permId, Xunit.TestContext.Current.CancellationToken);

        deleteResult.Outcome.Should().Be(DrivePermissionDeleteOutcome.Deleted);
        deleteResult.Error.Should().BeNull();
        var after = await _client.ListPermissionsAsync(folderId, Xunit.TestContext.Current.CancellationToken);
        after.Permissions.Should().BeEmpty();
    }

    [HumansFact]
    public async Task DeletePermissionAsync_MissingPermission_Returns404()
    {
        var folderId = _client.SeedFolder("Team A");

        var result = await _client.DeletePermissionAsync(folderId, "nope", Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(DrivePermissionDeleteOutcome.Failed);
        result.Error.Should().NotBeNull();
        result.Error!.StatusCode.Should().Be(404);
    }

    [HumansFact]
    public async Task GetFileAsync_AfterCreateFolder_ReturnsFolderMetadata()
    {
        var folderId = _client.SeedFolder("Team A");

        var fetched = await _client.GetFileAsync(folderId, Xunit.TestContext.Current.CancellationToken);

        fetched.Error.Should().BeNull();
        fetched.File.Should().NotBeNull();
        fetched.File!.Id.Should().Be(folderId);
        fetched.File.Name.Should().Be("Team A");
    }

    [HumansFact]
    public async Task GetFileAsync_MissingId_Returns404()
    {
        var result = await _client.GetFileAsync("nonexistent", Xunit.TestContext.Current.CancellationToken);

        result.File.Should().BeNull();
        result.Error!.StatusCode.Should().Be(404);
    }

    [HumansFact]
    public async Task SetInheritedPermissionsDisabledAsync_RoundTripsViaGetFile()
    {
        var folderId = _client.SeedFolder("Team A");

        var error = await _client.SetInheritedPermissionsDisabledAsync(folderId, disabled: true, ct: Xunit.TestContext.Current.CancellationToken);

        error.Should().BeNull();
        var fetched = await _client.GetFileAsync(folderId, Xunit.TestContext.Current.CancellationToken);
        fetched.File!.InheritedPermissionsDisabled.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetSharedDriveAsync_UnknownDrive_Returns404()
    {
        var result = await _client.GetSharedDriveAsync("nonexistent-drive", Xunit.TestContext.Current.CancellationToken);

        result.Drive.Should().BeNull();
        result.Error!.StatusCode.Should().Be(404);
    }

    [HumansFact]
    public async Task ListPermissionsAsync_UnknownFile_Returns404()
    {
        // Mirrors the real Drive API which returns HTTP 404 for missing
        // files rather than an empty permission list. Per Codex's P2
        // review on PR #302 — returning empty-success would mask
        // deleted / mistyped Google IDs during dev/QA.
        var result = await _client.ListPermissionsAsync("nonexistent-file", Xunit.TestContext.Current.CancellationToken);

        result.Permissions.Should().BeNull();
        result.Error.Should().NotBeNull();
        result.Error!.StatusCode.Should().Be(404);
    }

    [HumansFact]
    public async Task CreatePermissionAsync_UnknownFile_ReturnsFailed()
    {
        // Mirrors the real Drive API which returns HTTP 404 when the file
        // does not exist. Per Codex's P2 review on PR #302 — the stub
        // previously auto-created a permissions bucket for unknown ids,
        // which would let invalid / stale Google IDs pass dev/QA and only
        // fail in production with the real client.
        var result = await _client.CreatePermissionAsync(
            "nonexistent-file", "alice@nobodies.team", "writer", Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(DrivePermissionCreateOutcome.Failed);
        result.Error.Should().NotBeNull();
        result.Error!.StatusCode.Should().Be(404);
    }
}
