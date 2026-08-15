using System.Net;
using System.Text;
using AwesomeAssertions;
using Humans.Holded.Contracts;
using Humans.Holded.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;

namespace Humans.Holded.Tests.Services;

public class HoldedClientTransportTests
{
    private static HoldedClient Make(StubHandler handler, HoldedCallLog? log = null, FakeClock? clock = null) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.holded.com") },
            Options.Create(new HoldedClientOptions { ApiKey = "test-key" }),
            NullLogger<HoldedClient>.Instance,
            log ?? new HoldedCallLog(),
            clock ?? new FakeClock(Instant.FromUtc(2026, 8, 10, 12, 0)));

    [HumansFact]
    public async Task Sends_bearer_authorization_header()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(req =>
        {
            captured = req;
            return Respond(HttpStatusCode.OK, """{"items":[]}""");
        });

        var client = Make(handler);
        await client.ListExpenseAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        captured!.Headers.Authorization.Should().NotBeNull();
        captured.Headers.Authorization!.Scheme.Should().Be("Bearer");
        captured.Headers.Authorization!.Parameter.Should().Be("test-key");
        captured.Headers.Contains("key").Should().BeFalse();
    }

    [HumansFact]
    public async Task Retries_once_on_429_honoring_retry_after()
    {
        var requestCount = 0;
        var handler = new StubHandler(_ =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return resp;
            }
            return Respond(HttpStatusCode.OK, """{"items":[]}""");
        });

        var client = Make(handler);
        await client.ListExpenseAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        requestCount.Should().Be(2);
    }

    [HumansFact]
    public async Task Throws_transient_when_429_persists()
    {
        var handler = new StubHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return resp;
        });

        var client = Make(handler);
        var act = async () => await client.ListExpenseAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HoldedTransientException>();
    }

    [HumansFact]
    public async Task Records_call_in_log()
    {
        var handler = new StubHandler(_ =>
        {
            var resp = Respond(HttpStatusCode.OK, """{"items":[]}""");
            resp.Headers.Add("X-RateLimit-Remaining", "42");
            resp.Headers.Add("X-RateLimit-Window", "minute");
            return resp;
        });
        var log = new HoldedCallLog();
        var client = Make(handler, log);

        await client.ListExpenseAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        var records = log.DrainAll();
        records.Should().ContainSingle();
        var record = records[0];
        record.Endpoint.Should().Be(nameof(client.ListExpenseAccountsAsync));
        record.StatusCode.Should().Be(200);
        record.RateLimitRemaining.Should().Be(42);
        record.RateLimitWindow.Should().Be("minute");
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
