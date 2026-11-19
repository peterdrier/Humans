using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Google.Apis.CloudIdentity.v1;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using Humans.Base.Configuration;
using Humans.Google.Services.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Humans.Google.Tests.Infrastructure;

public sealed class TeamResourceGoogleClientTests
{
    [HumansTheory]
    [InlineData("crew-alias@nobodies.team", "crew@nobodies.team", "https://groups.google.com/a/nobodies.team/g/crew")]
    [InlineData("ops@other.example", "operations@other.example", "https://groups.google.com/a/other.example/g/operations")]
    [InlineData("crew@nobodies.team", "crew@nobodies.team", "https://groups.google.com/a/nobodies.team/g/crew")]
    public async Task LookupGroupAsync_UsesResolvedGroupEmailAndDomain(string submittedEmail, string resolvedEmail, string url)
    {
        using var handler = new GroupHandler(resolvedEmail);
        using var cloudIdentity = new CloudIdentityService(CreateInitializer(handler));
        var client = CreateClient(cloudIdentity);

        var result = await client.LookupGroupAsync(submittedEmail, TestContext.Current.CancellationToken);

        result.Error.Should().BeNull();
        result.Group.Should().Be(new ResolvedGroup("12345", resolvedEmail, "Crew", url));
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData("not-an-email")]
    public async Task LookupGroupAsync_MissingResolvedEmail_RefusesInsteadOfGuessing(string? resolvedEmail)
    {
        using var handler = new GroupHandler(resolvedEmail);
        using var cloudIdentity = new CloudIdentityService(CreateInitializer(handler));
        var client = CreateClient(cloudIdentity);

        var result = await client.LookupGroupAsync("crew@nobodies.team", TestContext.Current.CancellationToken);

        result.Group.Should().BeNull();
        result.Error.Should().NotBeNull();
    }

    [HumansTheory]
    [InlineData("crew@nobodies.team", "https://groups.google.com/a/nobodies.team/g/crew")]
    [InlineData("operations@other.example", "https://groups.google.com/a/other.example/g/operations")]
    public async Task StubLookupGroupAsync_UsesTheSuppliedDomain(string email, string url)
    {
        var client = new StubTeamResourceGoogleClient(NullLogger<StubTeamResourceGoogleClient>.Instance);

        var result = await client.LookupGroupAsync(email, TestContext.Current.CancellationToken);

        result.Group!.NormalizedEmail.Should().Be(email);
        result.Group.DisplayUrl.Should().Be(url);
    }

    [HumansTheory]
    [InlineData("Drive", false)]
    [InlineData("GroupLookup", false)]
    [InlineData("GroupGet", false)]
    [InlineData("Drive", true)]
    [InlineData("GroupLookup", true)]
    [InlineData("GroupGet", true)]
    public async Task Lookup_TransportFailureReturnsError_WhileCancellationPropagates(string boundary, bool cancelled)
    {
        using var request = new CancellationTokenSource();
        using var handler = new FailingHandler(boundary, cancelled, request);
        using BaseClientService sdk = string.Equals(boundary, "Drive", StringComparison.Ordinal)
            ? new DriveService(CreateInitializer(handler))
            : new CloudIdentityService(CreateInitializer(handler));
        var client = CreateClient(sdk, string.Equals(boundary, "Drive", StringComparison.Ordinal)
            ? "_driveService" : "_cloudIdentityService");
        GoogleClientError? error = null;
        async Task LookupAsync()
        {
            if (string.Equals(boundary, "Drive", StringComparison.Ordinal))
            {
                var result = await client.GetDriveItemAsync("folder-1", expectFolder: true, request.Token);
                result.Item.Should().BeNull();
                error = result.Error;
            }
            else
            {
                var result = await client.LookupGroupAsync("crew@nobodies.team", request.Token);
                result.Group.Should().BeNull();
                error = result.Error;
            }
        }

        if (cancelled)
        {
            var act = LookupAsync;
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            await LookupAsync();
            error.Should().NotBeNull();
            error!.StatusCode.Should().Be(0);
        }
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lookup_MissingCredentialFile_PropagatesConfigurationFailure(bool drive)
    {
        var client = new TeamResourceGoogleClient(
            Options.Create(new GoogleWorkspaceSettings
            {
                ServiceAccountKeyPath = Path.Combine(Path.GetTempPath(), $"missing-google-key-{Guid.NewGuid():N}.json")
            }), NullLogger<TeamResourceGoogleClient>.Instance, Substitute.For<IGoogleDrivePermissionsClient>());
        async Task LookupAsync()
        {
            if (drive) await client.GetDriveItemAsync("folder-1", expectFolder: true, TestContext.Current.CancellationToken);
            else await client.LookupGroupAsync("crew@nobodies.team", TestContext.Current.CancellationToken);
        }

        var act = LookupAsync;
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    private static BaseClientService.Initializer CreateInitializer(HttpMessageHandler handler)
    {
        var factory = Substitute.For<global::Google.Apis.Http.IHttpClientFactory>();
        factory.CreateHttpClient(Arg.Any<CreateHttpClientArgs>())
            .Returns(new ConfigurableHttpClient(new ConfigurableMessageHandler(handler)));
        return new BaseClientService.Initializer { HttpClientFactory = factory };
    }

    private static TeamResourceGoogleClient CreateClient(BaseClientService sdk, string field = "_cloudIdentityService")
    {
        var client = new TeamResourceGoogleClient(
            Options.Create(new GoogleWorkspaceSettings { Domain = "nobodies.team" }),
            NullLogger<TeamResourceGoogleClient>.Instance, Substitute.For<IGoogleDrivePermissionsClient>());
        typeof(TeamResourceGoogleClient).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, sdk);
        return client;
    }

    private sealed class FailingHandler(string boundary, bool cancelled, CancellationTokenSource request) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken)
        {
            if (string.Equals(boundary, "GroupGet", StringComparison.Ordinal)
                && message.RequestUri!.AbsolutePath.EndsWith("/groups:lookup", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"name\":\"groups/12345\"}", Encoding.UTF8, "application/json")
                };
            if (cancelled)
            {
                await request.CancelAsync();
                throw new OperationCanceledException(request.Token);
            }
            throw new HttpRequestException("Connection reset by peer.", new IOException("Connection reset by peer."));
        }
    }

    private sealed class GroupHandler(string? resolvedEmail) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath.EndsWith("/groups:lookup", StringComparison.Ordinal)
                ? "{\"name\":\"groups/12345\"}"
                : JsonSerializer.Serialize(new { name = "groups/12345", groupKey = new { id = resolvedEmail }, displayName = "Crew" });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
