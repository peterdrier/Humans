using System.Net;
using System.Reflection;
using System.Text;
using AwesomeAssertions;
using Google.Apis.DriveActivity.v2;
using Google.Apis.Http;
using Google.Apis.Services;
using Humans.Base.Configuration;
using Humans.Google.Contracts;
using Humans.Google.Services.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Google.Tests.Infrastructure;

public sealed class GoogleDriveActivityClientTests
{
    private const string ActivityPage = """
        {"activities":[{"actors":[{"administrator":{}}],
        "primaryActionDetail":{"permissionChange":{"addedPermissions":[{"role":"WRITER","anyone":{}}]}}}]}
        """;

    [HumansFact]
    public async Task QueryActivityAsync_TransientError_RetriesThenYieldsEvents()
    {
        using var handler = new SequenceHandler(
            (HttpStatusCode.InternalServerError, """{"error":{"code":500,"message":"Internal error encountered."}}"""),
            (HttpStatusCode.OK, ActivityPage));
        var (client, delays) = CreateClient(handler);

        var events = await QueryAllAsync(client);

        events.Should().ContainSingle().Which.PermissionChange!.AddedPermissions.Should().ContainSingle()
            .Which.IsAnyone.Should().BeTrue();
        handler.Calls.Should().Be(2);
        delays.Should().Equal(TimeSpan.FromSeconds(1));
    }

    [HumansFact]
    public async Task QueryActivityAsync_PermanentError_ThrowsWithoutRetry()
    {
        using var handler = new SequenceHandler(
            (HttpStatusCode.Forbidden, """{"error":{"code":403,"message":"permission denied"}}"""));
        var (client, delays) = CreateClient(handler);

        var query = () => QueryAllAsync(client);

        await query.Should().ThrowAsync<global::Google.GoogleApiException>();
        handler.Calls.Should().Be(1);
        delays.Should().BeEmpty();
    }

    private static async Task<List<DriveActivityEvent>> QueryAllAsync(GoogleDriveActivityClient client)
    {
        var events = new List<DriveActivityEvent>();
        await foreach (var activity in client.QueryActivityAsync("item-1", "2026-11-12T00:00:00Z",
                           Xunit.TestContext.Current.CancellationToken))
            events.Add(activity);
        return events;
    }

    private static (GoogleDriveActivityClient Client, List<TimeSpan> Delays) CreateClient(SequenceHandler handler)
    {
        var factory = Substitute.For<global::Google.Apis.Http.IHttpClientFactory>();
        factory.CreateHttpClient(Arg.Any<CreateHttpClientArgs>())
            .Returns(new ConfigurableHttpClient(new ConfigurableMessageHandler(handler)));
        var service = new DriveActivityService(new BaseClientService.Initializer { HttpClientFactory = factory });
        var delays = new List<TimeSpan>();
        var client = new GoogleDriveActivityClient(
            Options.Create(new GoogleWorkspaceSettings()), NullLogger<GoogleDriveActivityClient>.Instance)
        {
            Delay = (backoff, _) => { delays.Add(backoff); return Task.CompletedTask; },
        };
        // Replace only the transport: the real SDK request and error mapping run without credentials.
        typeof(GoogleDriveActivityClient).GetField("_activityService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, service);
        return (client, delays);
    }

    private sealed class SequenceHandler(params (HttpStatusCode Status, string Json)[] responses) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, json) = responses[Math.Min(Calls++, responses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
