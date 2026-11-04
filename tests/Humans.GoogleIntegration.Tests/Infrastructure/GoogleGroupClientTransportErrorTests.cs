using System.Reflection;
using AwesomeAssertions;
using Google.Apis.Admin.Directory.directory_v1;
using Google.Apis.CloudIdentity.v1;
using Google.Apis.Http;
using Google.Apis.Services;
using Humans.Base.Configuration;
using Humans.GoogleIntegration.Services.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.GoogleIntegration.Tests.Infrastructure;

/// <summary>A connection reset must come back as a status-0 error, so the reconcile pass carries on.</summary>
public sealed class GoogleGroupClientTransportErrorTests
{
    [HumansFact]
    public async Task LookupGroupIdAsync_TransportError_ReturnsStructuredError()
    {
        using var cloudIdentity = new CloudIdentityService(ResettingInitializer());
        var client = new GoogleGroupProvisioningClient(
            Options.Create(new GoogleWorkspaceSettings()), NullLogger<GoogleGroupProvisioningClient>.Instance);
        Inject(client, "_cloudIdentityService", cloudIdentity);

        var result = await client.LookupGroupIdAsync("team@nobodies.team", Xunit.TestContext.Current.CancellationToken);

        result.GroupNumericId.Should().BeNull();
        result.Error!.StatusCode.Should().Be(0);
    }

    [HumansFact]
    public async Task ListMembershipsAsync_TransportError_ReturnsStructuredError()
    {
        using var directory = new DirectoryService(ResettingInitializer());
        var client = new GoogleGroupMembershipClient(
            Options.Create(new GoogleWorkspaceSettings()), NullLogger<GoogleGroupMembershipClient>.Instance);
        Inject(client, "_directoryService", directory);

        var result = await client.ListMembershipsAsync("group-1", Xunit.TestContext.Current.CancellationToken);

        result.Memberships.Should().BeNull();
        result.Error!.StatusCode.Should().Be(0);
    }

    private static BaseClientService.Initializer ResettingInitializer()
    {
        var factory = Substitute.For<Google.Apis.Http.IHttpClientFactory>();
        factory.CreateHttpClient(Arg.Any<CreateHttpClientArgs>())
            .Returns(_ => new ConfigurableHttpClient(new ConfigurableMessageHandler(new ResettingHandler())));
        return new BaseClientService.Initializer { HttpClientFactory = factory };
    }

    private static void Inject<T>(T client, string field, object service) =>
        typeof(T).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, service);

    private sealed class ResettingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("An error occurred while sending the request.",
                new IOException("Unable to read data from the transport connection: Connection reset by peer."));
    }
}
