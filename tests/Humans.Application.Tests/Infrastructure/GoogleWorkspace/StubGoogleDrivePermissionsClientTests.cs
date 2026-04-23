using AwesomeAssertions;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Infrastructure.Services.GoogleWorkspace;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Humans.Application.Tests.Infrastructure.GoogleWorkspace;

/// <summary>
/// Contract tests for <see cref="StubGoogleDrivePermissionsClient"/>. These
/// exercise the idempotency and lifecycle contracts shared with the real
/// <see cref="GoogleDrivePermissionsClient"/>: folder create returns id +
/// webViewLink, permission adds deduplicate by email, deletes target an
/// existing permission id, and the file metadata round-trips the
/// inherited-permissions-disabled flag.
/// </summary>
public class StubGoogleDrivePermissionsClientTests
{
    private readonly StubGoogleDrivePermissionsClient _client =
        new(NullLogger<StubGoogleDrivePermissionsClient>.Instance);

    [Fact]
    public async Task CreateFolderAsync_ReturnsFolderWithIdAndLink()
    {
        var result = await _client.CreateFolderAsync("Team A", parentFolderId: null);

        result.Error.Should().BeNull();
        result.Folder.Should().NotBeNull();
        result.Folder!.Id.Should().NotBeNullOrEmpty();
        result.Folder.Name.Should().Be("Team A");
        result.Folder.WebViewLink.Should().StartWith("https://drive.google.com/drive/folders/");
    }

    [Fact]
    public async Task ListPermissionsAsync_EmptyFolder_ReturnsEmptyList()
    {
        var folder = await _client.CreateFolderAsync("Team A", parentFolderId: null);

        var result = await _client.ListPermissionsAsync(folder.Folder!.Id!);

        result.Error.Should().BeNull();
        result.Permissions.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task CreatePermissionAsync_NewEmail_ReturnsCreated()
    {
        var folder = await _client.CreateFolderAsync("Team A", parentFolderId: null);

        var result = await _client.CreatePermissionAsync(
            folder.Folder!.Id!, "alice@nobodies.team", "writer");

        result.Outcome.Should().Be(DrivePermissionCreateOutcome.Created);
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task CreatePermissionAsync_DuplicateEmail_ReturnsAlreadyExists()
    {
        var folder = await _client.CreateFolderAsync("Team A", parentFolderId: null);
        await _client.CreatePermissionAsync(folder.Folder!.Id!, "alice@nobodies.team", "writer");

        var second = await _client.CreatePermissionAsync(
            folder.Folder.Id!, "alice@nobodies.team", "reader");

        second.Outcome.Should().Be(DrivePermissionCreateOutcome.AlreadyExists,
            because: "the real client treats Google's 400 'already exists' as idempotent success");
    }

    [Fact]
    public async Task ListPermissionsAsync_AfterAdd_ContainsUserPermission()
    {
        var folder = await _client.CreateFolderAsync("Team A", parentFolderId: null);
        await _client.CreatePermissionAsync(folder.Folder!.Id!, "alice@nobodies.team", "writer");

        var result = await _client.ListPermissionsAsync(folder.Folder.Id!);

        result.Permissions.Should().ContainSingle();
        var perm = result.Permissions!.Single();
        perm.Type.Should().Be("user");
        perm.Role.Should().Be("writer");
        perm.EmailAddress.Should().Be("alice@nobodies.team");
        perm.IsInheritedOnly.Should().BeFalse(
            because: "stub permissions are treated as direct — tests covering inherited-vs-direct filtering belong to the real-client integration tests");
    }

    [Fact]
    public async Task DeletePermissionAsync_Existing_RemovesIt()
    {
        var folder = await _client.CreateFolderAsync("Team A", parentFolderId: null);
        await _client.CreatePermissionAsync(folder.Folder!.Id!, "alice@nobodies.team", "writer");
        var before = await _client.ListPermissionsAsync(folder.Folder.Id!);
        var permId = before.Permissions!.Single().Id!;

        var deleteError = await _client.DeletePermissionAsync(folder.Folder.Id!, permId);

        deleteError.Should().BeNull();
        var after = await _client.ListPermissionsAsync(folder.Folder.Id!);
        after.Permissions.Should().BeEmpty();
    }

    [Fact]
    public async Task DeletePermissionAsync_MissingPermission_Returns404()
    {
        var folder = await _client.CreateFolderAsync("Team A", parentFolderId: null);

        var result = await _client.DeletePermissionAsync(folder.Folder!.Id!, "nope");

        result.Should().NotBeNull();
        result!.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task GetFileAsync_AfterCreateFolder_ReturnsFolderMetadata()
    {
        var folder = await _client.CreateFolderAsync("Team A", parentFolderId: null);

        var fetched = await _client.GetFileAsync(folder.Folder!.Id!);

        fetched.Error.Should().BeNull();
        fetched.File.Should().NotBeNull();
        fetched.File!.Id.Should().Be(folder.Folder.Id);
        fetched.File.Name.Should().Be("Team A");
    }

    [Fact]
    public async Task GetFileAsync_MissingId_Returns404()
    {
        var result = await _client.GetFileAsync("nonexistent");

        result.File.Should().BeNull();
        result.Error!.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task SetInheritedPermissionsDisabledAsync_RoundTripsViaGetFile()
    {
        var folder = await _client.CreateFolderAsync("Team A", parentFolderId: null);

        var error = await _client.SetInheritedPermissionsDisabledAsync(folder.Folder!.Id!, disabled: true);

        error.Should().BeNull();
        var fetched = await _client.GetFileAsync(folder.Folder.Id!);
        fetched.File!.InheritedPermissionsDisabled.Should().BeTrue();
    }

    [Fact]
    public async Task GetSharedDriveAsync_UnknownDrive_Returns404()
    {
        var result = await _client.GetSharedDriveAsync("nonexistent-drive");

        result.Drive.Should().BeNull();
        result.Error!.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task ListPermissionsAsync_UnknownFile_Returns404()
    {
        // Mirrors the real Drive API which returns HTTP 404 for missing
        // files rather than an empty permission list. Per Codex's P2
        // review on PR #302 — returning empty-success would mask
        // deleted / mistyped Google IDs during dev/QA.
        var result = await _client.ListPermissionsAsync("nonexistent-file");

        result.Permissions.Should().BeNull();
        result.Error.Should().NotBeNull();
        result.Error!.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task CreatePermissionAsync_UnknownFile_ReturnsFailed()
    {
        // Mirrors the real Drive API which returns HTTP 404 when the file
        // does not exist. Per Codex's P2 review on PR #302 — the stub
        // previously auto-created a permissions bucket for unknown ids,
        // which would let invalid / stale Google IDs pass dev/QA and only
        // fail in production with the real client.
        var result = await _client.CreatePermissionAsync(
            "nonexistent-file", "alice@nobodies.team", "writer");

        result.Outcome.Should().Be(DrivePermissionCreateOutcome.Failed);
        result.Error.Should().NotBeNull();
        result.Error!.StatusCode.Should().Be(404);
    }
}
