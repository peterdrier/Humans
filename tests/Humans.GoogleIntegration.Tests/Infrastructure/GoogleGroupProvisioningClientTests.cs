using System.Reflection;
using AwesomeAssertions;
using Google.Apis.CloudIdentity.v1;
using Google.Apis.Http;
using Google.Apis.Services;
using Humans.Base.Configuration;
using Humans.GoogleIntegration.Services.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.GoogleIntegration.Tests.Infrastructure;

public sealed class GoogleGroupProvisioningClientTests
{
    [HumansFact]
    public async Task LookupGroupIdAsync_TransportError_ReturnsStructuredError()
    {
        var factory = Substitute.For<Google.Apis.Http.IHttpClientFactory>();
        factory.CreateHttpClient(Arg.Any<CreateHttpClientArgs>())
            .Returns(new ConfigurableHttpClient(new ConfigurableMessageHandler(new ResettingHandler())));
        using var cloudIdentity = new CloudIdentityService(new BaseClientService.Initializer { HttpClientFactory = factory });
        var client = new GoogleGroupProvisioningClient(
            Options.Create(new GoogleWorkspaceSettings()), NullLogger<GoogleGroupProvisioningClient>.Instance);
        typeof(GoogleGroupProvisioningClient).GetField("_cloudIdentityService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, cloudIdentity);

        var result = await client.LookupGroupIdAsync("team@nobodies.team", Xunit.TestContext.Current.CancellationToken);

        result.GroupNumericId.Should().BeNull();
        result.Error.Should().NotBeNull();
        result.Error!.StatusCode.Should().Be(0);
    }

    private sealed class ResettingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("An error occurred while sending the request.",
                new IOException("Unable to read data from the transport connection: Connection reset by peer."));
    }
}
