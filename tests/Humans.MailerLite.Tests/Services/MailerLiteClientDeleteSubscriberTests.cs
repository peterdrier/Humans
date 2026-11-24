using System.Net;
using System.Text;
using Xunit;
using AwesomeAssertions;
using Humans.MailerLite.Services.MailerLite;

namespace Humans.MailerLite.Tests.Services;

/// <summary>
/// GDPR erasure deletes the person's MailerLite subscriber. The client's subscriber snapshot has no TTL,
/// so the local eviction has to happen on every outcome the remote calls success —
/// including the 404 that means "already gone there".
/// </summary>
public class MailerLiteClientDeleteSubscriberTests
{
    private const string Erased = "erased@example.org";

    [HumansFact]
    public async Task DeleteSubscriberAsync_UsesPermanentForgetByResolvedId()
    {
        var handler = new DeleteHandler(HttpStatusCode.OK);
        var client = BuildClient(handler);

        await client.DeleteSubscriberAsync(Erased, TestContext.Current.CancellationToken);

        handler.Requests.Should().Equal(
            (HttpMethod.Get, "/api/subscribers/" + Uri.EscapeDataString(Erased)),
            (HttpMethod.Post, "/api/subscribers/1/forget"));
    }

    [HumansFact]
    public async Task DeleteSubscriberAsync_MissingLookup_EvictsWithoutWriting()
    {
        var handler = new DeleteHandler(HttpStatusCode.OK) { LookupStatus = HttpStatusCode.NotFound };
        var client = BuildClient(handler);
        await DrainAsync(client);

        await client.DeleteSubscriberAsync(Erased, TestContext.Current.CancellationToken);

        (await SnapshotEmailsAsync(client)).Should().NotContain(Erased);
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [HumansTheory]
    [InlineData("{}")]
    [InlineData("""{"data":{"id":" ","email":"erased@example.org","status":"active"}}""")]
    [InlineData("""{"data":{"id":"2","email":"other@example.org","status":"active"}}""")]
    public async Task DeleteSubscriberAsync_InvalidLookup_PreservesSnapshotAndDoesNotForget(string payload)
    {
        var handler = new DeleteHandler(HttpStatusCode.OK) { LookupPayload = payload };
        var client = BuildClient(handler);
        await DrainAsync(client);

        var erase = () => client.DeleteSubscriberAsync(Erased, TestContext.Current.CancellationToken);

        (await erase.Should().ThrowAsync<HttpRequestException>()).WithMessage("*subscriber*");
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
        (await SnapshotEmailsAsync(client)).Should().Contain(Erased);
    }

    [HumansFact]
    public async Task DeleteSubscriberAsync_EvictsFromTheSnapshot()
    {
        var client = BuildClient(new DeleteHandler(HttpStatusCode.NoContent));
        await DrainAsync(client);

        await client.DeleteSubscriberAsync(Erased, TestContext.Current.CancellationToken);

        (await SnapshotEmailsAsync(client)).Should().NotContain(Erased);
    }

    [HumansFact]
    public async Task DeleteSubscriberAsync_NotFoundAtTheRemote_StillEvictsFromTheSnapshot()
    {
        var client = BuildClient(new DeleteHandler(HttpStatusCode.NotFound));
        await DrainAsync(client);

        await client.DeleteSubscriberAsync(Erased, TestContext.Current.CancellationToken);

        (await SnapshotEmailsAsync(client)).Should().NotContain(Erased,
            "an erased human's address must not survive in the no-TTL subscriber cache");
    }

    [HumansTheory]
    [InlineData("active", HttpStatusCode.NoContent)]
    [InlineData("unsubscribed", HttpStatusCode.NoContent)]
    [InlineData("unconfirmed", HttpStatusCode.NoContent)]
    [InlineData("bounced", HttpStatusCode.NoContent)]
    [InlineData("junk", HttpStatusCode.NoContent)]
    [InlineData("Active", HttpStatusCode.NoContent)]
    [InlineData("UNSUBSCRIBED", HttpStatusCode.NoContent)]
    [InlineData("Unconfirmed", HttpStatusCode.NoContent)]
    [InlineData("BOUNCED", HttpStatusCode.NoContent)]
    [InlineData("Junk", HttpStatusCode.NoContent)]
    [InlineData("active", HttpStatusCode.NotFound)]
    public async Task DeleteSubscriberAsync_UpdatesStatusTotals(string status, HttpStatusCode response)
    {
        var client = BuildClient(new DeleteHandler(response, status));
        var before = await client.GetAccountSummaryAsync(TestContext.Current.CancellationToken);
        (before.ActiveCount + before.UnsubscribedCount + before.UnconfirmedCount
            + before.BouncedCount + before.JunkCount).Should().Be(1);

        await client.DeleteSubscriberAsync(Erased, TestContext.Current.CancellationToken);

        var after = await client.GetAccountSummaryAsync(TestContext.Current.CancellationToken);
        (after.ActiveCount + after.UnsubscribedCount + after.UnconfirmedCount
            + after.BouncedCount + after.JunkCount).Should().Be(0);
    }

    [HumansFact]
    public async Task Unknown_status_remains_in_the_snapshot_and_logs_a_warning()
    {
        var logger = new CapturingLogger<MailerLiteClient>();
        var client = BuildClient(new DeleteHandler(HttpStatusCode.NoContent, "future-status"), logger);

        var summary = await client.GetAccountSummaryAsync(TestContext.Current.CancellationToken);

        (summary.ActiveCount + summary.UnsubscribedCount + summary.UnconfirmedCount
            + summary.BouncedCount + summary.JunkCount).Should().Be(0);
        (await SnapshotEmailsAsync(client)).Should().Contain(Erased);
        var warning = logger.Entries.Should().ContainSingle().Subject;
        warning.Level.Should().Be(Microsoft.Extensions.Logging.LogLevel.Warning);
        warning.Message.Should().Contain("future-status");
        warning.Exception.Should().BeNull();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteSubscriberAsync_Failure_PreservesSnapshotAndStatusTotals(bool lookupFails)
    {
        var client = BuildClient(new DeleteHandler(lookupFails ? HttpStatusCode.OK : HttpStatusCode.InternalServerError)
        { LookupStatus = lookupFails ? HttpStatusCode.InternalServerError : HttpStatusCode.OK });
        var before = await client.GetAccountSummaryAsync(TestContext.Current.CancellationToken);

        var delete = () => client.DeleteSubscriberAsync(Erased, TestContext.Current.CancellationToken);
        await delete.Should().ThrowAsync<HttpRequestException>();

        (await SnapshotEmailsAsync(client)).Should().Contain(Erased);
        (await client.GetAccountSummaryAsync(TestContext.Current.CancellationToken)).Should().Be(before);
    }

    private static async Task DrainAsync(MailerLiteClient client)
    {
        (await SnapshotEmailsAsync(client)).Should().Contain(Erased, "the snapshot must be warm first");
    }

    private static async Task<List<string>> SnapshotEmailsAsync(MailerLiteClient client)
    {
        var emails = new List<string>();
        await foreach (var s in client.ListSubscribersAsync(TestContext.Current.CancellationToken))
            emails.Add(s.Email);
        return emails;
    }

    private static MailerLiteClient BuildClient(HttpMessageHandler handler, Microsoft.Extensions.Logging.ILogger<MailerLiteClient>? logger = null) =>
        new(new StubHttpClientFactory(handler), NodaTime.SystemClock.Instance,
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MailerLiteClient>.Instance);

    // One subscriber on the first subscribers page, empty thereafter; erasure answers with the status under test.
    private sealed class DeleteHandler(HttpStatusCode deleteStatus, string status = "active") : HttpMessageHandler
    {
        private bool _subscribersServed;
        public HttpStatusCode LookupStatus { get; init; } = HttpStatusCode.OK;
        public string? LookupPayload { get; init; }
        public List<(HttpMethod Method, string Path)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            Requests.Add((req.Method, path));
            if (req.Method == HttpMethod.Delete || req.Method == HttpMethod.Post)
                return Task.FromResult(new HttpResponseMessage(deleteStatus));
            if (string.Equals(path, "/api/subscribers/" + Uri.EscapeDataString(Erased), StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(LookupStatus)
                {
                    Content = new StringContent(LookupPayload ?? System.Text.Json.JsonSerializer.Serialize(new
                    {
                        data = new { id = "1", email = Erased, status },
                    }), Encoding.UTF8, "application/json"),
                });
            string body;
            if (path.Contains("/api/groups", StringComparison.Ordinal))
            {
                body = """{"data":[],"meta":{"current_page":1,"last_page":1}}""";
            }
            else if (path.Contains("/api/subscribers", StringComparison.Ordinal) && !_subscribersServed)
            {
                _subscribersServed = true;
                body =
                    "{\"data\":[{\"id\":\"1\",\"email\":\"" + Erased + "\",\"status\":\"" + status + "\"," +
                    "\"source\":\"api\",\"subscribed_at\":null,\"unsubscribed_at\":null," +
                    "\"opted_in_at\":null,\"fields\":{},\"groups\":[]}]," +
                    "\"meta\":{\"next_cursor\":null}}";
            }
            else
            {
                body = """{"data":[],"meta":{"next_cursor":null}}""";
            }

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
