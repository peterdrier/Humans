using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Google.Apis.CloudIdentity.v1;
using Google.Apis.Http;
using Google.Apis.Services;
using Humans.Base.Configuration;
using Humans.GoogleIntegration.Services.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Humans.GoogleIntegration.Tests.Infrastructure;

public sealed class TeamResourceGoogleClientTests
{
    [HumansTheory]
    [InlineData("crew-alias@nobodies.team", "crew@nobodies.team", "https://groups.google.com/a/nobodies.team/g/crew")]
    [InlineData("ops@other.example", "operations@other.example", "https://groups.google.com/a/other.example/g/operations")]
    [InlineData("crew@nobodies.team", "crew@nobodies.team", "https://groups.google.com/a/nobodies.team/g/crew")]
    public async Task LookupGroupAsync_UsesResolvedGroupEmailAndDomain(string submittedEmail, string resolvedEmail, string url)
    {
        using var handler = new GroupHandler(resolvedEmail);
        using var cloudIdentity = CreateCloudIdentity(handler);
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
        using var cloudIdentity = CreateCloudIdentity(handler);
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

    private static CloudIdentityService CreateCloudIdentity(HttpMessageHandler handler)
    {
        var factory = Substitute.For<Google.Apis.Http.IHttpClientFactory>();
        factory.CreateHttpClient(Arg.Any<CreateHttpClientArgs>())
            .Returns(new ConfigurableHttpClient(new ConfigurableMessageHandler(handler)));
        return new CloudIdentityService(new BaseClientService.Initializer { HttpClientFactory = factory });
    }

    private static TeamResourceGoogleClient CreateClient(CloudIdentityService cloudIdentity)
    {
        var client = new TeamResourceGoogleClient(
            Options.Create(new GoogleWorkspaceSettings { Domain = "nobodies.team" }),
            NullLogger<TeamResourceGoogleClient>.Instance, Substitute.For<IGoogleDrivePermissionsClient>());
        typeof(TeamResourceGoogleClient).GetField("_cloudIdentityService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, cloudIdentity);
        return client;
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
