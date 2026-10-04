using System.Net;
using System.Reflection;
using System.Text;
using AwesomeAssertions;
using Google.Apis.Admin.Directory.directory_v1;
using Google.Apis.Http;
using Google.Apis.Services;
using Humans.Base.Configuration;
using Humans.GoogleIntegration.Services.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Humans.GoogleIntegration.Tests.Infrastructure;

public sealed class WorkspaceUserDirectoryClientTests
{
    [HumansTheory]
    [InlineData("2026-09-29T00:10:00Z", "2026-09-30T00:20:00Z")]
    [InlineData("2026-09-29T02:10:00+02:00", "2026-09-29T20:20:00-04:00")]
    public async Task ListAccountsAsync_NormalizesVendorTimestampsToUtc(string created, string lastLogin)
    {
        using var handler = new ResponseHandler($$"""
            {"users":[{"primaryEmail":"ana@nobodies.team","creationTime":"{{created}}","lastLoginTime":"{{lastLogin}}"}]}
            """);
        using var service = CreateDirectory(handler);
        var client = CreateClient(service);

        var accounts = await client.ListAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        var account = accounts.Should().ContainSingle().Subject;
        account.CreationTime.Should().Be(new DateTime(2026, 9, 29, 0, 10, 0, DateTimeKind.Utc));
        account.CreationTime.Kind.Should().Be(DateTimeKind.Utc);
        account.LastLoginTime.Should().Be(new DateTime(2026, 9, 30, 0, 20, 0, DateTimeKind.Utc));
        account.LastLoginTime!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [HumansFact]
    public async Task ListAccountsAsync_PreservesMissingTimestampDefaults()
    {
        using var handler = new ResponseHandler("""{"users":[{"primaryEmail":"ana@nobodies.team"}]}""");
        using var service = CreateDirectory(handler);
        var client = CreateClient(service);

        var accounts = await client.ListAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        var account = accounts.Should().ContainSingle().Subject;
        account.CreationTime.Should().Be(DateTime.MinValue);
        account.LastLoginTime.Should().BeNull();
    }

    private static DirectoryService CreateDirectory(ResponseHandler handler)
    {
        var factory = Substitute.For<Google.Apis.Http.IHttpClientFactory>();
        factory.CreateHttpClient(Arg.Any<CreateHttpClientArgs>())
            .Returns(new ConfigurableHttpClient(new ConfigurableMessageHandler(handler)));
        return new DirectoryService(new BaseClientService.Initializer { HttpClientFactory = factory });
    }

    private static WorkspaceUserDirectoryClient CreateClient(DirectoryService service)
    {
        var client = new WorkspaceUserDirectoryClient(Options.Create(new GoogleWorkspaceSettings()),
            NullLogger<WorkspaceUserDirectoryClient>.Instance);
        // Exercise the real SDK response and account mapping without credentials or network IO.
        typeof(WorkspaceUserDirectoryClient).GetField("_directoryService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, service);
        return client;
    }

    private sealed class ResponseHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }
}
