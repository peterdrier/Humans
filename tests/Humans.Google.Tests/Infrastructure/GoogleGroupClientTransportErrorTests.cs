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
    [Xunit.InlineData(GooglePagedRead.Accounts, PageTokenSequence.Repeat)]
    [Xunit.InlineData(GooglePagedRead.Accounts, PageTokenSequence.Cycle)]
    [Xunit.InlineData(GooglePagedRead.Accounts, PageTokenSequence.Valid)]
    [Xunit.InlineData(GooglePagedRead.Groups, PageTokenSequence.Repeat)]
    [Xunit.InlineData(GooglePagedRead.Groups, PageTokenSequence.Cycle)]
    [Xunit.InlineData(GooglePagedRead.Groups, PageTokenSequence.Valid)]
    [Xunit.InlineData(GooglePagedRead.Members, PageTokenSequence.Repeat)]
    [Xunit.InlineData(GooglePagedRead.Members, PageTokenSequence.Cycle)]
    [Xunit.InlineData(GooglePagedRead.Members, PageTokenSequence.Valid)]
    [Xunit.InlineData(GooglePagedRead.Permissions, PageTokenSequence.Repeat)]
    [Xunit.InlineData(GooglePagedRead.Permissions, PageTokenSequence.Cycle)]
    [Xunit.InlineData(GooglePagedRead.Permissions, PageTokenSequence.Valid)]
    [Xunit.InlineData(GooglePagedRead.Activity, PageTokenSequence.Repeat)]
    [Xunit.InlineData(GooglePagedRead.Activity, PageTokenSequence.Cycle)]
    [Xunit.InlineData(GooglePagedRead.Activity, PageTokenSequence.Valid)]
    public async Task Paginated_reads_reject_repeated_tokens_without_completing_a_partial_list(GooglePagedRead kind, PageTokenSequence sequence)
    {
        using var handler = new PageHandler(kind, sequence);
        var initializer = CreateInitializer(handler);
        using BaseClientService sdk = kind switch
        {
            GooglePagedRead.Permissions => new DriveService(initializer),
            GooglePagedRead.Activity => new DriveActivityService(initializer),
            _ => new DirectoryService(initializer)
        };
        var settings = Options.Create(new GoogleWorkspaceSettings());
        GoogleClientError? error = null;
        async Task<int> FetchAsync()
        {
            switch (kind)
            {
                case GooglePagedRead.Accounts:
                    var accounts = new WorkspaceUserDirectoryClient(settings, NullLogger<WorkspaceUserDirectoryClient>.Instance);
                    Inject(accounts, "_directoryService", sdk);
                    return (await accounts.ListAccountsAsync(Xunit.TestContext.Current.CancellationToken)).Count;
                case GooglePagedRead.Groups:
                    var groups = new GoogleDirectoryClient(settings, NullLogger<GoogleDirectoryClient>.Instance);
                    Inject(groups, "_directoryService", sdk);
                    var groupResult = await groups.ListDomainGroupsAsync(Xunit.TestContext.Current.CancellationToken);
                    error = groupResult.Error;
                    if (error is not null) groupResult.Groups.Should().BeNull();
                    return groupResult.Groups?.Count ?? 0;
                case GooglePagedRead.Members:
                    var members = new GoogleGroupMembershipClient(settings, NullLogger<GoogleGroupMembershipClient>.Instance);
                    Inject(members, "_directoryService", sdk);
                    var memberResult = await members.ListMembershipsAsync("group", Xunit.TestContext.Current.CancellationToken);
                    error = memberResult.Error;
                    if (error is not null) memberResult.Memberships.Should().BeNull();
                    return memberResult.Memberships?.Count ?? 0;
                case GooglePagedRead.Permissions:
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
                    await foreach (var _ in activity.QueryActivityAsync("file", "2026-11-20T00:00:00Z",
                                       Xunit.TestContext.Current.CancellationToken))
                        count++;
                    return count;
            }
        }

        if (sequence is PageTokenSequence.Valid)
        {
            (await FetchAsync()).Should().Be(3);
            error.Should().BeNull();
        }
        else if (kind is GooglePagedRead.Groups or GooglePagedRead.Members or GooglePagedRead.Permissions)
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
        handler.Requests.Count.Should().Be(sequence is PageTokenSequence.Repeat ? 2 : 3);
        if (kind is not GooglePagedRead.Activity)
            handler.Requests[1].Should().Contain("cursor-a");
    }

    [HumansTheory]
    [Xunit.InlineData(GoogleStructuredCall.Groups, GoogleCallFailure.Http)]
    [Xunit.InlineData(GoogleStructuredCall.Groups, GoogleCallFailure.Io)]
    [Xunit.InlineData(GoogleStructuredCall.Groups, GoogleCallFailure.Cancel)]
    [Xunit.InlineData(GoogleStructuredCall.Groups, GoogleCallFailure.Credentials)]
    [Xunit.InlineData(GoogleStructuredCall.Permissions, GoogleCallFailure.Http)]
    [Xunit.InlineData(GoogleStructuredCall.Permissions, GoogleCallFailure.Io)]
    [Xunit.InlineData(GoogleStructuredCall.Permissions, GoogleCallFailure.Cancel)]
    [Xunit.InlineData(GoogleStructuredCall.Permissions, GoogleCallFailure.Credentials)]
    [Xunit.InlineData(GoogleStructuredCall.File, GoogleCallFailure.Http)]
    [Xunit.InlineData(GoogleStructuredCall.File, GoogleCallFailure.Io)]
    [Xunit.InlineData(GoogleStructuredCall.File, GoogleCallFailure.Cancel)]
    [Xunit.InlineData(GoogleStructuredCall.File, GoogleCallFailure.Credentials)]
    [Xunit.InlineData(GoogleStructuredCall.Drive, GoogleCallFailure.Http)]
    [Xunit.InlineData(GoogleStructuredCall.Drive, GoogleCallFailure.Io)]
    [Xunit.InlineData(GoogleStructuredCall.Drive, GoogleCallFailure.Cancel)]
    [Xunit.InlineData(GoogleStructuredCall.Drive, GoogleCallFailure.Credentials)]
    [Xunit.InlineData(GoogleStructuredCall.Create, GoogleCallFailure.Http)]
    [Xunit.InlineData(GoogleStructuredCall.Create, GoogleCallFailure.Io)]
    [Xunit.InlineData(GoogleStructuredCall.Create, GoogleCallFailure.Cancel)]
    [Xunit.InlineData(GoogleStructuredCall.Create, GoogleCallFailure.Credentials)]
    [Xunit.InlineData(GoogleStructuredCall.Update, GoogleCallFailure.Http)]
    [Xunit.InlineData(GoogleStructuredCall.Update, GoogleCallFailure.Io)]
    [Xunit.InlineData(GoogleStructuredCall.Update, GoogleCallFailure.Cancel)]
    [Xunit.InlineData(GoogleStructuredCall.Update, GoogleCallFailure.Credentials)]
    [Xunit.InlineData(GoogleStructuredCall.Delete, GoogleCallFailure.Http)]
    [Xunit.InlineData(GoogleStructuredCall.Delete, GoogleCallFailure.Io)]
    [Xunit.InlineData(GoogleStructuredCall.Delete, GoogleCallFailure.Cancel)]
    [Xunit.InlineData(GoogleStructuredCall.Delete, GoogleCallFailure.Credentials)]
    [Xunit.InlineData(GoogleStructuredCall.Inherit, GoogleCallFailure.Http)]
    [Xunit.InlineData(GoogleStructuredCall.Inherit, GoogleCallFailure.Io)]
    [Xunit.InlineData(GoogleStructuredCall.Inherit, GoogleCallFailure.Cancel)]
    [Xunit.InlineData(GoogleStructuredCall.Inherit, GoogleCallFailure.Credentials)]
    [Xunit.InlineData(GoogleStructuredCall.Folder, GoogleCallFailure.Http)]
    [Xunit.InlineData(GoogleStructuredCall.Folder, GoogleCallFailure.Io)]
    [Xunit.InlineData(GoogleStructuredCall.Folder, GoogleCallFailure.Cancel)]
    [Xunit.InlineData(GoogleStructuredCall.Folder, GoogleCallFailure.Credentials)]
    public async Task Structured_calls_keep_transport_failures_separate_from_cancellation_and_credentials(GoogleStructuredCall kind, GoogleCallFailure failure)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Xunit.TestContext.Current.CancellationToken);
        if (failure is GoogleCallFailure.Cancel) await cancellation.CancelAsync();
        using var handler = new ResettingHandler(failure is GoogleCallFailure.Io);
        var initializer = CreateInitializer(handler);
        using BaseClientService sdk = kind is GoogleStructuredCall.Groups ? new DirectoryService(initializer) : new DriveService(initializer);
        var settings = Options.Create(new GoogleWorkspaceSettings
        {
            Domain = "example.org",
            ServiceAccountKeyPath = $"/tmp/debt98-missing-{Guid.NewGuid():N}/credentials.json",
        });
        var groupLogger = new Humans.Testing.CapturingLogger<GoogleDirectoryClient>();
        var driveLogger = new Humans.Testing.CapturingLogger<GoogleDrivePermissionsClient>();

        async Task<GoogleClientError?> FetchAsync()
        {
            if (kind is GoogleStructuredCall.Groups)
            {
                var client = new GoogleDirectoryClient(settings, groupLogger);
                if (failure is not GoogleCallFailure.Credentials) Inject(client, "_directoryService", sdk);
                var result = await client.ListDomainGroupsAsync(cancellation.Token);
                result.Groups.Should().BeNull();
                return result.Error;
            }
            var drive = new GoogleDrivePermissionsClient(settings, driveLogger);
            if (failure is not GoogleCallFailure.Credentials) Inject(drive, "_driveService", sdk);
            if (kind is GoogleStructuredCall.Create)
            {
                var result = await drive.CreatePermissionAsync("file", "person@example.org", "reader", cancellation.Token);
                result.Outcome.Should().Be(DrivePermissionCreateOutcome.Failed);
                return result.Error;
            }
            if (kind is GoogleStructuredCall.Update)
                return await drive.UpdatePermissionAsync("file", "permission", "reader", cancellation.Token);
            if (kind is GoogleStructuredCall.Delete)
            {
                var result = await drive.DeletePermissionAsync("file", "permission", cancellation.Token);
                result.Outcome.Should().Be(DrivePermissionDeleteOutcome.Failed);
                return result.Error;
            }
            if (kind is GoogleStructuredCall.Inherit)
                return await drive.SetInheritedPermissionsDisabledAsync("file", true, cancellation.Token);
            if (kind is GoogleStructuredCall.Folder)
            {
                var result = await drive.CreateFolderAsync("parent", "Folder", cancellation.Token);
                result.FolderId.Should().BeNull();
                return result.Error;
            }
            if (kind is GoogleStructuredCall.Permissions)
            {
                var result = await drive.ListPermissionsAsync("file", cancellation.Token);
                result.Permissions.Should().BeNull();
                return result.Error;
            }
            if (kind is GoogleStructuredCall.File)
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
        if (failure is GoogleCallFailure.Cancel) await fetch.Should().ThrowAsync<OperationCanceledException>();
        else if (failure is GoogleCallFailure.Credentials) await fetch.Should().ThrowAsync<IOException>();
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

    [HumansTheory]
    [Xunit.InlineData(GoogleGroupCredentialOperation.CreateGroup)]
    [Xunit.InlineData(GoogleGroupCredentialOperation.LookupGroup)]
    [Xunit.InlineData(GoogleGroupCredentialOperation.ReadSettings)]
    [Xunit.InlineData(GoogleGroupCredentialOperation.WriteSettings)]
    [Xunit.InlineData(GoogleGroupCredentialOperation.ListMembers)]
    [Xunit.InlineData(GoogleGroupCredentialOperation.CreateMember)]
    [Xunit.InlineData(GoogleGroupCredentialOperation.DeleteMember)]
    public async Task Group_calls_propagate_credential_file_failures(GoogleGroupCredentialOperation operation)
    {
        var settings = Options.Create(new GoogleWorkspaceSettings
        {
            ServiceAccountKeyPath = $"/tmp/debt100-missing-{Guid.NewGuid():N}/credentials.json",
        });
        var provisioning = new GoogleGroupProvisioningClient(settings, NullLogger<GoogleGroupProvisioningClient>.Instance);
        var membership = new GoogleGroupMembershipClient(settings, NullLogger<GoogleGroupMembershipClient>.Instance);
        var ct = Xunit.TestContext.Current.CancellationToken;
        Task FetchAsync() => operation switch
        {
            GoogleGroupCredentialOperation.CreateGroup => provisioning.CreateGroupAsync("team@example.org", "Team", "Description", ct),
            GoogleGroupCredentialOperation.LookupGroup => provisioning.LookupGroupIdAsync("team@example.org", ct),
            GoogleGroupCredentialOperation.ReadSettings => provisioning.GetGroupSettingsAsync("team@example.org", ct),
            GoogleGroupCredentialOperation.WriteSettings => provisioning.UpdateGroupSettingsAsync("team@example.org", Humans.Google.Services.GroupSettingsPolicy.BuildExpected(new Humans.Google.Contracts.GoogleWorkspaceGroupOptions()), ct),
            GoogleGroupCredentialOperation.ListMembers => membership.ListMembershipsAsync("group", ct),
            GoogleGroupCredentialOperation.CreateMember => membership.CreateMembershipAsync("group", "person@example.org", ct),
            _ => membership.DeleteMembershipAsync("groups/group/memberships/person@example.org", ct),
        };

        var fetch = FetchAsync;
        await fetch.Should().ThrowAsync<IOException>();
    }

    [HumansFact]
    public async Task DeleteMembershipAsync_refuses_malformed_names_before_loading_credentials()
    {
        var client = new GoogleGroupMembershipClient(Options.Create(new GoogleWorkspaceSettings()),
            NullLogger<GoogleGroupMembershipClient>.Instance);

        var error = await client.DeleteMembershipAsync("malformed", Xunit.TestContext.Current.CancellationToken);

        error.Should().NotBeNull();
        error.RawMessage.Should().Contain("Malformed membership resource name");
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

    private sealed class PageHandler(GooglePagedRead kind, PageTokenSequence sequence) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            if (Requests.Count > 4) throw new InvalidOperationException("Test transport exceeded four pages.");
            var next = Requests.Count == 1 ? "cursor-a"
                : sequence is PageTokenSequence.Repeat ? "cursor-a"
                : Requests.Count == 2 ? "cursor-b"
                : sequence is PageTokenSequence.Cycle ? "cursor-a" : null;
            var collection = kind switch
            {
                GooglePagedRead.Accounts => "\"users\":[{\"primaryEmail\":\"a@example.org\"}]",
                GooglePagedRead.Groups => "\"groups\":[{\"id\":\"g\",\"email\":\"g@example.org\"}]",
                GooglePagedRead.Members => "\"members\":[{\"id\":\"m\",\"email\":\"a@example.org\"}]",
                GooglePagedRead.Permissions => "\"permissions\":[{\"id\":\"p\",\"type\":\"user\",\"role\":\"reader\",\"emailAddress\":\"a@example.org\"}]",
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

public enum GooglePagedRead { Accounts, Groups, Members, Permissions, Activity }

public enum PageTokenSequence { Repeat, Cycle, Valid }

public enum GoogleStructuredCall { Groups, Permissions, File, Drive, Create, Update, Delete, Inherit, Folder }

public enum GoogleCallFailure { Http, Io, Cancel, Credentials }

public enum GoogleGroupCredentialOperation { CreateGroup, LookupGroup, ReadSettings, WriteSettings, ListMembers, CreateMember, DeleteMember }
