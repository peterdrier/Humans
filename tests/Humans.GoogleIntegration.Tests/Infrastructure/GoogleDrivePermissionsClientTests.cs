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
        error!.StatusCode.Should().Be(403);
        error.RawMessage.Should().Be("permission denied");
        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Patch);
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

    private sealed class RecordingHandler(string responseJson, HttpStatusCode status = HttpStatusCode.OK)
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
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }
}
