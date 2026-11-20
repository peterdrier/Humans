using System.Net;
using System.Text;
using System.Text.Json;
using Google.Apis.Drive.v3;
using Google.Apis.DriveActivity.v2;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using AwesomeAssertions;
using Google.Apis.Admin.Directory.directory_v1;
using Google.Apis.CloudIdentity.v1;
using Google.Apis.Http;
using Google.Apis.Services;
using Humans.Base.Configuration;
using Humans.Google.Services.Workspace;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Google.Tests.Infrastructure;

/// <summary>A connection reset must come back as a status-0 error, so the reconcile pass carries on.</summary>
public sealed class GoogleGroupClientTransportErrorTests
{
    [HumansFact]
    public async Task LookupGroupIdAsync_TransportError_ReturnsStructuredError()
    {
        using var cloudIdentity = new CloudIdentityService(CreateInitializer());
        var logger = new Humans.Testing.CapturingLogger<GoogleGroupProvisioningClient>();
        var client = new GoogleGroupProvisioningClient(
            Options.Create(new GoogleWorkspaceSettings()), logger);
        Inject(client, "_cloudIdentityService", cloudIdentity);

        var result = await client.LookupGroupIdAsync("team@nobodies.team", Xunit.TestContext.Current.CancellationToken);

        result.GroupNumericId.Should().BeNull();
        result.Error!.StatusCode.Should().Be(0);
        var warning = logger.Entries.Should().ContainSingle().Subject;
        warning.Level.Should().Be(Microsoft.Extensions.Logging.LogLevel.Warning);
        warning.Exception.Should().BeNull();
        warning.Message.Should().Contain("An error occurred while sending the request.");
    }

    [HumansFact]
    public async Task ListMembershipsAsync_TransportError_ReturnsStructuredError()
    {
        using var directory = new DirectoryService(CreateInitializer());
        var logger = new Humans.Testing.CapturingLogger<GoogleGroupMembershipClient>();
        var client = new GoogleGroupMembershipClient(
            Options.Create(new GoogleWorkspaceSettings()), logger);
        Inject(client, "_directoryService", directory);

        var result = await client.ListMembershipsAsync("group-1", Xunit.TestContext.Current.CancellationToken);

        result.Memberships.Should().BeNull();
        result.Error!.StatusCode.Should().Be(0);
        var warning = logger.Entries.Should().ContainSingle().Subject;
        warning.Level.Should().Be(Microsoft.Extensions.Logging.LogLevel.Warning);
        warning.Exception.Should().BeNull();
        warning.Message.Should().Contain("An error occurred while sending the request.");
    }

    [HumansTheory]
    [Xunit.InlineData("accounts", "repeat")]
    [Xunit.InlineData("accounts", "cycle")]
    [Xunit.InlineData("accounts", "valid")]
    [Xunit.InlineData("groups", "repeat")]
    [Xunit.InlineData("groups", "cycle")]
    [Xunit.InlineData("groups", "valid")]
    [Xunit.InlineData("members", "repeat")]
    [Xunit.InlineData("members", "cycle")]
    [Xunit.InlineData("members", "valid")]
    [Xunit.InlineData("permissions", "repeat")]
    [Xunit.InlineData("permissions", "cycle")]
    [Xunit.InlineData("permissions", "valid")]
    [Xunit.InlineData("activity", "repeat")]
    [Xunit.InlineData("activity", "cycle")]
    [Xunit.InlineData("activity", "valid")]
    public async Task Paginated_reads_reject_repeated_tokens_without_completing_a_partial_list(string kind, string sequence)
    {
        using var handler = new PageHandler(kind, sequence);
        var initializer = CreateInitializer(handler);
        using BaseClientService sdk = kind switch
        {
            "permissions" => new DriveService(initializer),
            "activity" => new DriveActivityService(initializer),
            _ => new DirectoryService(initializer)
        };
        var settings = Options.Create(new GoogleWorkspaceSettings());
        GoogleClientError? error = null;
        async Task<int> FetchAsync()
        {
            switch (kind)
            {
                case "accounts":
                    var accounts = new WorkspaceUserDirectoryClient(settings, NullLogger<WorkspaceUserDirectoryClient>.Instance);
                    Inject(accounts, "_directoryService", sdk);
                    return (await accounts.ListAccountsAsync(Xunit.TestContext.Current.CancellationToken)).Count;
                case "groups":
                    var groups = new GoogleDirectoryClient(settings, NullLogger<GoogleDirectoryClient>.Instance);
                    Inject(groups, "_directoryService", sdk);
                    var groupResult = await groups.ListDomainGroupsAsync(Xunit.TestContext.Current.CancellationToken);
                    error = groupResult.Error;
                    if (error is not null) groupResult.Groups.Should().BeNull();
                    return groupResult.Groups?.Count ?? 0;
                case "members":
                    var members = new GoogleGroupMembershipClient(settings, NullLogger<GoogleGroupMembershipClient>.Instance);
                    Inject(members, "_directoryService", sdk);
                    var memberResult = await members.ListMembershipsAsync("group", Xunit.TestContext.Current.CancellationToken);
                    error = memberResult.Error;
                    if (error is not null) memberResult.Memberships.Should().BeNull();
                    return memberResult.Memberships?.Count ?? 0;
                case "permissions":
                    var permissions = new GoogleDrivePermissionsClient(settings, NullLogger<GoogleDrivePermissionsClient>.Instance);
                    Inject(permissions, "_driveService", sdk);
                    var permissionResult = await permissions.ListPermissionsAsync("file", Xunit.TestContext.Current.CancellationToken);
                    error = permissionResult.Error;
                    if (error is not null) permissionResult.Permissions.Should().BeNull();
                    return permissionResult.Permissions?.Count ?? 0;
                default:
                    var activity = new GoogleDriveActivityClient(settings, NullLogger<GoogleDriveActivityClient>.Instance);
                    Inject(activity, "_activityService", sdk);
                    var count = 0;
                    await foreach (var item in activity.QueryActivityAsync("file", "2026-11-20T00:00:00Z",
                                       Xunit.TestContext.Current.CancellationToken))
                        count++;
                    return count;
            }
        }

        if (string.Equals(sequence, "valid", StringComparison.Ordinal))
        {
            (await FetchAsync()).Should().Be(3);
            error.Should().BeNull();
        }
        else if (kind is "groups" or "members" or "permissions")
        {
            (await FetchAsync()).Should().Be(0);
            error.Should().NotBeNull();
            error!.IsTransient.Should().BeTrue();
            error.RawMessage.Should().Contain("repeated page token");
        }
        else
        {
            var fetch = FetchAsync;
            await fetch.Should().ThrowAsync<InvalidOperationException>().WithMessage("*repeated page token*");
        }
        handler.Requests.Count.Should().Be(string.Equals(sequence, "repeat", StringComparison.Ordinal) ? 2 : 3);
        if (!string.Equals(kind, "activity", StringComparison.Ordinal))
            handler.Requests[1].Should().Contain("cursor-a");
    }

    [HumansTheory]
    [Xunit.InlineData("groups", "http")]
    [Xunit.InlineData("groups", "io")]
    [Xunit.InlineData("groups", "cancel")]
    [Xunit.InlineData("groups", "credentials")]
    [Xunit.InlineData("permissions", "http")]
    [Xunit.InlineData("permissions", "io")]
    [Xunit.InlineData("permissions", "cancel")]
    [Xunit.InlineData("permissions", "credentials")]
    [Xunit.InlineData("file", "http")]
    [Xunit.InlineData("file", "io")]
    [Xunit.InlineData("file", "cancel")]
    [Xunit.InlineData("file", "credentials")]
    [Xunit.InlineData("drive", "http")]
    [Xunit.InlineData("drive", "io")]
    [Xunit.InlineData("drive", "cancel")]
    [Xunit.InlineData("drive", "credentials")]
    public async Task Structured_reads_keep_transport_failures_separate_from_cancellation_and_credentials(string kind, string failure)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Xunit.TestContext.Current.CancellationToken);
        if (failure is "cancel") await cancellation.CancelAsync();
        using var handler = new ResettingHandler(failure is "io");
        var initializer = CreateInitializer(handler);
        using BaseClientService sdk = kind is "groups" ? new DirectoryService(initializer) : new DriveService(initializer);
        var settings = Options.Create(new GoogleWorkspaceSettings
        {
            Domain = "example.org",
            ServiceAccountKeyPath = $"/tmp/debt98-missing-{Guid.NewGuid():N}/credentials.json",
        });
        var groupLogger = new Humans.Testing.CapturingLogger<GoogleDirectoryClient>();
        var driveLogger = new Humans.Testing.CapturingLogger<GoogleDrivePermissionsClient>();

        async Task<GoogleClientError?> FetchAsync()
        {
            if (kind is "groups")
            {
                var client = new GoogleDirectoryClient(settings, groupLogger);
                if (failure is not "credentials") Inject(client, "_directoryService", sdk);
                var result = await client.ListDomainGroupsAsync(cancellation.Token);
                result.Groups.Should().BeNull();
                return result.Error;
            }
            var drive = new GoogleDrivePermissionsClient(settings, driveLogger);
            if (failure is not "credentials") Inject(drive, "_driveService", sdk);
            if (kind is "permissions")
            {
                var result = await drive.ListPermissionsAsync("file", cancellation.Token);
                result.Permissions.Should().BeNull();
                return result.Error;
            }
            if (kind is "file")
            {
                var result = await drive.GetFileAsync("file", cancellation.Token);
                result.File.Should().BeNull();
                return result.Error;
            }
            var sharedDrive = await drive.GetSharedDriveAsync("drive", cancellation.Token);
            sharedDrive.Drive.Should().BeNull();
            return sharedDrive.Error;
        }

        var fetch = FetchAsync;
        if (failure is "cancel") await fetch.Should().ThrowAsync<OperationCanceledException>();
        else if (failure is "credentials") await fetch.Should().ThrowAsync<IOException>();
        else
        {
            var error = await fetch();
            error.Should().NotBeNull();
            error.StatusCode.Should().Be(0);
            var logs = groupLogger.Entries.Select(entry => (entry.Level, entry.Exception, entry.Message))
                .Concat(driveLogger.Entries.Select(entry => (entry.Level, entry.Exception, entry.Message)));
            var warning = logs.Where(entry => entry.Level >= Microsoft.Extensions.Logging.LogLevel.Warning)
                .Should().ContainSingle().Subject;
            warning.Level.Should().Be(Microsoft.Extensions.Logging.LogLevel.Warning);
            warning.Exception.Should().BeNull();
            warning.Message.Should().Contain(error.RawMessage!);
        }
    }

    private static BaseClientService.Initializer CreateInitializer(HttpMessageHandler? handler = null)
    {
        var factory = Substitute.For<global::Google.Apis.Http.IHttpClientFactory>();
        factory.CreateHttpClient(Arg.Any<CreateHttpClientArgs>())
            .Returns(_ => new ConfigurableHttpClient(new ConfigurableMessageHandler(handler ?? new ResettingHandler())));
        return new BaseClientService.Initializer { HttpClientFactory = factory };
    }

    private static void Inject<T>(T client, string field, object service) =>
        typeof(T).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, service);

    private sealed class PageHandler(string kind, string sequence) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            if (Requests.Count > 4) throw new InvalidOperationException("Test transport exceeded four pages.");
            var next = Requests.Count == 1 ? "cursor-a"
                : string.Equals(sequence, "repeat", StringComparison.Ordinal) ? "cursor-a"
                : Requests.Count == 2 ? "cursor-b"
                : string.Equals(sequence, "cycle", StringComparison.Ordinal) ? "cursor-a" : null;
            var collection = kind switch
            {
                "accounts" => "\"users\":[{\"primaryEmail\":\"a@example.org\"}]",
                "groups" => "\"groups\":[{\"id\":\"g\",\"email\":\"g@example.org\"}]",
                "members" => "\"members\":[{\"id\":\"m\",\"email\":\"a@example.org\"}]",
                "permissions" => "\"permissions\":[{\"id\":\"p\",\"type\":\"user\",\"role\":\"reader\",\"emailAddress\":\"a@example.org\"}]",
                _ => "\"activities\":[{}]"
            };
            var json = "{" + collection + (next is null ? "" : ",\"nextPageToken\":" + JsonSerializer.Serialize(next)) + "}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ResettingHandler(bool streamOnly = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw (streamOnly ? new IOException("Connection reset by peer.")
                : new HttpRequestException("An error occurred while sending the request.",
                new IOException("Unable to read data from the transport connection: Connection reset by peer.")));
    }
}
