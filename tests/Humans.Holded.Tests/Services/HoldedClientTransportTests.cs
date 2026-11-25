using System.Net;
using System.Text;
using AwesomeAssertions;
using Humans.Holded.Contracts;
using Humans.Holded.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Holded.Tests.Services;

public class HoldedClientTransportTests
{
    [HumansTheory]
    [Xunit.InlineData("not a URI", false)]
    [Xunit.InlineData("", false)]
    [Xunit.InlineData("/relative", false)]
    [Xunit.InlineData("ftp://holded.example", false)]
    [Xunit.InlineData("https://holded.example", true)]
    [Xunit.InlineData("http://holded.example", true)]
    public async Task Register_Endpoint_ResolvesBeforeValidation(string baseUrl, bool valid)
    {
        var services = new ServiceCollection();
        new Section().Register(services, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Holded:BaseUrl"] = baseUrl }).Build());
        services.Configure<HoldedClientOptions>(o => o.ApiKey = "test-key");
        services.AddSingleton<IClock>(new FakeClock(Instant.FromUtc(2026, 8, 10, 12, 0)));
        services.AddSingleton<IOrgTimeZone>(FixedOrgTimeZone.Madrid);
        var calls = 0;
        services.AddHttpClient<IHoldedClient, HoldedClient>().ConfigurePrimaryHttpMessageHandler(() => new StubHandler(request =>
        {
            calls++;
            request.RequestUri!.Host.Should().Be("holded.example");
            return Respond(HttpStatusCode.OK, request.Method == HttpMethod.Get ? """{"items":[]}""" : """{"id":"account-1"}""");
        }));
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IHoldedClient>();
        var read = () => client.ListExpenseAccountsAsync(Xunit.TestContext.Current.CancellationToken);
        var write = () => client.CreateExpenseAccountAsync(60000000, "Test account", Xunit.TestContext.Current.CancellationToken);

        client.IsConfigured.Should().Be(valid);
        if (valid)
        {
            (await read()).Should().BeEmpty();
            (await write()).Should().Be("account-1");
        }
        else
        {
            await read.Should().ThrowAsync<HoldedPermanentException>().WithMessage("*absolute HTTP(S)*");
            await write.Should().ThrowAsync<HoldedPermanentException>().WithMessage("*absolute HTTP(S)*");
        }
        calls.Should().Be(valid ? 2 : 0);
    }

    private static HoldedClient Make(StubHandler handler, HoldedCallLog? log = null, FakeClock? clock = null, string apiKey = "test-key") =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.holded.com") },
            Options.Create(new HoldedClientOptions { ApiKey = apiKey }),
            NullLogger<HoldedClient>.Instance,
            log ?? new HoldedCallLog(),
            clock ?? new FakeClock(Instant.FromUtc(2026, 8, 10, 12, 0)), FixedOrgTimeZone.Madrid);

    [HumansTheory]
    [Xunit.InlineData("")]
    [Xunit.InlineData(" \t")]
    [Xunit.InlineData("secret\nhidden")]
    [Xunit.InlineData("secret\r\nX-Injected: hidden")]
    public async Task Requests_InvalidKey_RefuseBeforeHttp(string apiKey)
    {
        using var assertions = new AwesomeAssertions.Execution.AssertionScope();
        var calls = 0;
        var log = new HoldedCallLog();
        var client = Make(new StubHandler(request =>
        {
            calls++;
            return Respond(HttpStatusCode.OK, request.Method == HttpMethod.Get ? """{"items":[]}""" : """{"id":"account-1"}""");
        }), log: log, apiKey: apiKey);
        var ct = Xunit.TestContext.Current.CancellationToken;
        client.IsConfigured.Should().BeFalse();
        Func<Task>[] operations =
        [
            () => client.ListExpenseAccountsAsync(ct),
            () => client.CreateExpenseAccountAsync(60000000, "Test account", ct),
            () => client.ApproveSalesDocumentAsync(HoldedSalesDocumentKind.Invoice, "document", ct),
        ];
        foreach (var operation in operations)
            await operation.Should().ThrowAsync<HoldedPermanentException>()
                .WithMessage("HOLDED_API_KEY_V2 must be nonblank and valid in an HTTP authorization header.");
        calls.Should().Be(0);
        log.DrainAll().Should().BeEmpty();
    }

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
    public async Task A_403_for_a_missing_key_permission_names_the_permission()
    {
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.Forbidden,
            """{"error":"Insufficient permissions: accounting:payments.write required"}""")));

        var act = () => client.ListExpenseAccountsAsync(Xunit.TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<HoldedPermanentException>()).Which.Message
            .Should().Be("Humans' Holded key lacks accounting:payments.write; add it in Holded > Settings > API");
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
    public async Task Retry_after_HTTP_date_preserves_a_fractional_wait()
    {
        var requestCount = 0;
        var retryAt = new DateTimeOffset(2026, 8, 10, 12, 0, 1, TimeSpan.Zero);
        var clock = new FakeClock(Instant.FromDateTimeOffset(retryAt) - Duration.FromMilliseconds(900));
        var handler = new StubHandler(_ =>
        {
            requestCount++;
            if (requestCount > 1) return Respond(HttpStatusCode.OK, """{"items":[]}""");
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAt);
            return response;
        });
        var client = Make(handler, clock: clock);
        using var request = new CancellationTokenSource();
        var pending = client.ListExpenseAccountsAsync(request.Token);

        await request.CancelAsync();
        try
        {
            await pending;
            Xunit.Assert.Fail("The future Retry-After date must delay the second request.");
        }
        catch (OperationCanceledException)
        {
            requestCount.Should().Be(1);
        }
    }

    [HumansTheory]
    [Xunit.InlineData(HoldedSalesDocumentKind.Invoice)]
    [Xunit.InlineData(HoldedSalesDocumentKind.SalesReceipt)]
    public async Task Does_not_retry_bodyless_approval_posts_on_429(HoldedSalesDocumentKind kind)
    {
        var requestCount = 0;
        var handler = new StubHandler(req =>
        {
            req.Method.Should().Be(HttpMethod.Post);
            requestCount++;
            if (requestCount > 1) return Respond(HttpStatusCode.OK, "{}");
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        });
        var client = Make(handler);

        var act = () => client.ApproveSalesDocumentAsync(kind, "document", Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HoldedTransientException>();
        requestCount.Should().Be(1, "an empty request body does not make a remote write safe to retry");
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
