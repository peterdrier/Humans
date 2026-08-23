using System.Net;
using System.Text;
using AwesomeAssertions;
using Humans.MailerLite.Services.MailerLite;

namespace Humans.MailerLite.Tests.Services;

/// <summary>
/// GDPR erasure deletes the person's MailerLite subscriber
/// (nobodies-collective/Humans#853). The client's subscriber snapshot has no TTL,
/// so the local eviction has to happen on every outcome the remote calls success —
/// including the 404 that means "already gone there".
/// </summary>
public class MailerLiteClientDeleteSubscriberTests
{
    private const string Erased = "erased@example.org";

    [HumansFact]
    public async Task DeleteSubscriberAsync_EvictsFromTheSnapshot()
    {
        var client = BuildClient(new DeleteHandler(HttpStatusCode.NoContent));
        await DrainAsync(client);

        await client.DeleteSubscriberAsync(Erased, Xunit.TestContext.Current.CancellationToken);

        (await SnapshotEmailsAsync(client)).Should().NotContain(Erased);
    }

    [HumansFact]
    public async Task DeleteSubscriberAsync_NotFoundAtTheRemote_StillEvictsFromTheSnapshot()
    {
        var client = BuildClient(new DeleteHandler(HttpStatusCode.NotFound));
        await DrainAsync(client);

        await client.DeleteSubscriberAsync(Erased, Xunit.TestContext.Current.CancellationToken);

        (await SnapshotEmailsAsync(client)).Should().NotContain(Erased,
            "an erased human's address must not survive in the no-TTL subscriber cache");
    }

    private static async Task DrainAsync(MailerLiteClient client)
    {
        (await SnapshotEmailsAsync(client)).Should().Contain(Erased, "the snapshot must be warm first");
    }

    private static async Task<List<string>> SnapshotEmailsAsync(MailerLiteClient client)
    {
        var emails = new List<string>();
        await foreach (var s in client.ListSubscribersAsync(Xunit.TestContext.Current.CancellationToken))
            emails.Add(s.Email);
        return emails;
    }

    private static MailerLiteClient BuildClient(HttpMessageHandler handler) =>
        new(new StubHttpClientFactory(handler), NodaTime.SystemClock.Instance,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MailerLiteClient>.Instance);

    // One subscriber on the first subscribers page, empty thereafter; DELETE answers
    // with the status under test.
    private sealed class DeleteHandler(HttpStatusCode deleteStatus) : HttpMessageHandler
    {
        private bool _subscribersServed;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(deleteStatus));

            var path = req.RequestUri!.AbsolutePath;
            string body;
            if (path.Contains("/api/groups", StringComparison.Ordinal))
            {
                body = """{"data":[],"meta":{"current_page":1,"last_page":1}}""";
            }
            else if (path.Contains("/api/subscribers", StringComparison.Ordinal) && !_subscribersServed)
            {
                _subscribersServed = true;
                body =
                    "{\"data\":[{\"id\":\"1\",\"email\":\"" + Erased + "\",\"status\":\"active\"," +
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
