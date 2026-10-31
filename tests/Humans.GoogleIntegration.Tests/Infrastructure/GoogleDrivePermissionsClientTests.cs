using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using Humans.Base.Configuration;
using Humans.GoogleIntegration.Services.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.GoogleIntegration.Tests.Infrastructure;

public sealed class GoogleDrivePermissionsClientTests
{
    [HumansFact]
    public async Task ListPermissionsAsync_PreservesDirectGrantAndInheritedRoles()
    {
        using var handler = new RecordingHandler("""
            {"permissions":[{"id":"perm-1","type":"user","role":"writer","emailAddress":"alice@nobodies.team",
            "permissionDetails":[{"inherited":true,"role":"reader"},{"inherited":true,"role":"commenter"},
            {"inherited":false,"role":"writer"}]}]}
            """);
        using var drive = CreateDrive(handler);
        var client = CreateClient(drive);

        var result = await client.ListPermissionsAsync("folder-1", Xunit.TestContext.Current.CancellationToken);

        var permission = result.Permissions.Should().ContainSingle().Subject;
        permission.Role.Should().Be("writer");
        permission.HasDirectComponent.Should().BeTrue();
        permission.HasInheritedComponent.Should().BeTrue();
        permission.InheritedRoles.Should().BeEquivalentTo(["reader", "commenter"]);
        handler.Requests.Should().ContainSingle().Which.Url.Should().Contain("permissionDetails");
    }

    [HumansFact]
    public async Task UpdatePermissionAsync_PatchesOnlyRoleOnSharedDrivePermission()
    {
        using var handler = new RecordingHandler("""{"id":"perm-1","role":"reader"}""");
        using var drive = CreateDrive(handler);
        var client = CreateClient(drive);

        var error = await client.UpdatePermissionAsync("folder-1", "perm-1", "reader", Xunit.TestContext.Current.CancellationToken);

        error.Should().BeNull();
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Patch);
        var uri = new Uri(request.Url);
        uri.AbsolutePath.Should().Be("/drive/v3/files/folder-1/permissions/perm-1");
        uri.Query.Should().Contain("supportsAllDrives=true");
        using var body = JsonDocument.Parse(request.Body!);
        body.RootElement.EnumerateObject().Should().ContainSingle().Which.Name.Should().Be("role");
        body.RootElement.GetProperty("role").GetString().Should().Be("reader");
    }

    [HumansFact]
    public async Task UpdatePermissionAsync_ForbiddenResponse_PreservesFailure()
    {
        using var handler = new RecordingHandler("""
            {"error":{"code":403,"message":"permission denied",
            "errors":[{"domain":"global","reason":"forbidden","message":"permission denied"}]}}
            """, HttpStatusCode.Forbidden);
        using var drive = CreateDrive(handler);
        var client = CreateClient(drive);

        var error = await client.UpdatePermissionAsync("folder-1", "perm-1", "reader", Xunit.TestContext.Current.CancellationToken);

        error.Should().NotBeNull();
        error.StatusCode.Should().Be(403);
        error.RawMessage.Should().Be("permission denied");
        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Patch);
    }

    [HumansTheory]
    [Xunit.InlineData("full")]
    [Xunit.InlineData("root-denied")]
    [Xunit.InlineData("root-omits-drive-id")]
    [Xunit.InlineData("root-file-denied")]
    public async Task GetDriveItemAsync_UsesCompleteSharedPathOrLeafName(string scenario)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var rootTarget = string.Equals(scenario, "root-omits-drive-id", StringComparison.Ordinal);
        var rootDenied = string.Equals(scenario, "root-denied", StringComparison.Ordinal);
        var rootFileDenied = string.Equals(scenario, "root-file-denied", StringComparison.Ordinal);
        const string leafJson = """
            {"id":"folder-1","name":"Finance","mimeType":"application/vnd.google-apps.folder",
             "parents":["parent-1"],"driveId":"drive-1","webViewLink":"https://drive.google.com/drive/folders/folder-1"}
            """;
        const string rootJson = """
            {"id":"drive-1","name":"Drive","mimeType":"application/vnd.google-apps.folder",
             "webViewLink":"https://drive.google.com/drive/folders/drive-1"}
            """;
        using var handler = new RecordingHandler(leafJson, responseForPath: path =>
        {
            if (path.EndsWith("/drives/drive-1", StringComparison.Ordinal))
                return rootDenied
                    ? ("""{"error":{"code":403,"message":"permission denied"}}""", HttpStatusCode.Forbidden)
                    : ("""{"id":"drive-1","name":"Nobodies"}""", HttpStatusCode.OK);
            if (rootFileDenied && path.EndsWith("/files/drive-1", StringComparison.Ordinal))
                return ("""{"error":{"code":403,"message":"permission denied"}}""", HttpStatusCode.Forbidden);
            if (path.EndsWith("/files/parent-1", StringComparison.Ordinal))
                return ("""{"id":"parent-1","name":"Administration","parents":["drive-1"],"driveId":"drive-1"}""", HttpStatusCode.OK);
            return (rootTarget ? rootJson : leafJson, HttpStatusCode.OK);
        });
        using var drive = CreateDrive(handler);
        var permissions = Substitute.For<IGoogleDrivePermissionsClient>();
        permissions.GetFileAsync("parent-1", ct).Returns(new DriveFileMetadataResult(
            new DriveFileMetadata("parent-1", "Administration", ["drive-1"], "drive-1", null), null));
        permissions.GetFileAsync("drive-1", ct).Returns(rootFileDenied
            ? new DriveFileMetadataResult(null, new GoogleClientError(403, "permission denied"))
            : new DriveFileMetadataResult(new DriveFileMetadata("drive-1", "Drive", null, null, null), null));
        permissions.GetSharedDriveAsync("drive-1", ct).Returns(rootDenied
            ? new SharedDriveMetadataResult(null, new GoogleClientError(403, "permission denied"))
            : new SharedDriveMetadataResult(new SharedDriveMetadata("drive-1", "Nobodies"), null));
        var client = new TeamResourceGoogleClient(
            Options.Create(new GoogleWorkspaceSettings()), NullLogger<TeamResourceGoogleClient>.Instance, permissions);
        typeof(TeamResourceGoogleClient).GetField("_driveService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, drive);
        var targetId = rootTarget ? "drive-1" : "folder-1";

        var result = await client.GetDriveItemAsync(targetId, expectFolder: true, ct);

        result.Error.Should().BeNull();
        result.Item!.FullPath.Should().Be(rootTarget ? "Nobodies" : rootDenied ? "Finance" : "Nobodies / Administration / Finance");
        result.Item.Name.Should().Be(rootTarget ? "Drive" : "Finance");
        result.Item.IsFolder.Should().BeTrue();
        result.Item.WebViewLink.Should().Be($"https://drive.google.com/drive/folders/{targetId}");
    }

    private static DriveService CreateDrive(RecordingHandler handler)
    {
        var factory = Substitute.For<Google.Apis.Http.IHttpClientFactory>();
        factory.CreateHttpClient(Arg.Any<CreateHttpClientArgs>())
            .Returns(new ConfigurableHttpClient(new ConfigurableMessageHandler(handler)));
        return new DriveService(new BaseClientService.Initializer { HttpClientFactory = factory });
    }

    private static GoogleDrivePermissionsClient CreateClient(DriveService drive)
    {
        var client = new GoogleDrivePermissionsClient(
            Options.Create(new GoogleWorkspaceSettings()), NullLogger<GoogleDrivePermissionsClient>.Instance);
        // Replace only the transport: exercise the real SDK request, response, and error mapping
        // without loading service-account credentials or sending anything to Google.
        typeof(GoogleDrivePermissionsClient).GetField("_driveService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, drive);
        return client;
    }

    private sealed class RecordingHandler(string responseJson, HttpStatusCode status = HttpStatusCode.OK,
        Func<string, (string Json, HttpStatusCode Status)>? responseForPath = null)
        : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = null;
            if (request.Content is not null)
            {
                await using var contentStream = await request.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(
                    request.Content.Headers.ContentEncoding.Contains("gzip", StringComparer.OrdinalIgnoreCase)
                        ? new GZipStream(contentStream, CompressionMode.Decompress)
                        : contentStream);
                body = await reader.ReadToEndAsync(cancellationToken);
            }
            Requests.Add((request.Method, request.RequestUri!.ToString(), body));
            var response = responseForPath?.Invoke(request.RequestUri.AbsolutePath) ?? (responseJson, status);
            return new HttpResponseMessage(response.Item2)
            {
                Content = new StringContent(response.Item1, Encoding.UTF8, "application/json")
            };
        }
    }
}
