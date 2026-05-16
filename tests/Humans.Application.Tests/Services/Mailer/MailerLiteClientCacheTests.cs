using System.Net;
using System.Text;
using AwesomeAssertions;
using Humans.Infrastructure.Services.Mailer;

namespace Humans.Application.Tests.Services.Mailer;

public class MailerLiteClientCacheTests
{
    [HumansFact]
    public async Task SecondRead_DoesNotHitHttp()
    {
        var handler = new CountingHandler();
        var client = BuildClient(handler);

        _ = await client.GetAccountSummaryAsync();
        var callsAfterFirst = handler.Calls;
        _ = await client.GetAccountSummaryAsync();
        _ = await client.ListGroupsAsync();
        await foreach (var _ in client.ListSubscribersAsync()) { /* drain */ }

        handler.Calls.Should().Be(callsAfterFirst, "every read after the first must be served from the cache");
    }

    [HumansFact]
    public async Task LastFetchedAt_IsNullUntilFirstRead()
    {
        var client = BuildClient(new CountingHandler());
        client.LastFetchedAt.Should().BeNull();

        _ = await client.GetAccountSummaryAsync();

        client.LastFetchedAt.Should().NotBeNull();
    }

    [HumansFact]
    public async Task RefreshAsync_RepullsFromHttp()
    {
        var handler = new CountingHandler();
        var client = BuildClient(handler);

        _ = await client.GetAccountSummaryAsync();
        var callsBeforeRefresh = handler.Calls;

        await client.RefreshAsync();

        handler.Calls.Should().BeGreaterThan(callsBeforeRefresh, "Refresh must re-fetch from MailerLite");
    }

    private static MailerLiteClient BuildClient(HttpMessageHandler handler) =>
        new(new StubHttpClientFactory(handler), NodaTime.SystemClock.Instance,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MailerLiteClient>.Instance);

    // Returns one empty subscriber page and one empty groups page. The client
    // treats an empty payload as "end of list", so a single call settles each
    // collection and we can simply count handler invocations.
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Calls++;
            var path = req.RequestUri!.AbsolutePath;
            var body = path.Contains("/api/groups", StringComparison.Ordinal)
                ? """{"data":[],"meta":{"current_page":1,"last_page":1}}"""
                : """{"data":[],"meta":{"next_cursor":null}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://example.test/") };
    }
}
