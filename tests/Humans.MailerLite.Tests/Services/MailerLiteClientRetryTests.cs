using System.Net;
using System.Net.Http.Headers;
using AwesomeAssertions;
using Humans.MailerLite.Services.MailerLite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Humans.MailerLite.Tests.Services;

public class MailerLiteClientRetryTests
{
    private const string HumansGroupPage =
        """{"data":[{"id":"42","name":"Humans - Test","created_at":"2026-01-01 00:00:00","active_count":0,"unsubscribed_count":0,"unconfirmed_count":0,"bounced_count":0,"junk_count":0}],"meta":{"current_page":1,"last_page":1}}""";

    [HumansFact]
    public async Task AssignSubscriberToGroupAsync_RetriesAfter429_AndSucceeds()
    {
        var handler = new ScriptedHandler();
        // Cache pre-populate: empty subscriber page, then a Humans-managed group.
        handler.EnqueueJson(HttpStatusCode.OK, """{"data":[],"meta":{"next_cursor":null}}""");
        handler.EnqueueJson(HttpStatusCode.OK, HumansGroupPage);
        handler.Enqueue429(retryAfterSeconds: 0);
        handler.EnqueueJson(HttpStatusCode.OK, "{}");
        var client = NewClient(handler);
        var callsBeforeAssign = 2;

        await client.AssignSubscriberToGroupAsync("sub-1", "42", Xunit.TestContext.Current.CancellationToken);

        handler.Calls.Should().Be(callsBeforeAssign + 2,
            "the 429 must be retried once and succeed on the second attempt");
    }

    [HumansFact]
    public async Task AssignSubscriberToGroupAsync_GivesUpAfterBoundedRetries()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueJson(HttpStatusCode.OK, """{"data":[],"meta":{"next_cursor":null}}""");
        handler.EnqueueJson(HttpStatusCode.OK, HumansGroupPage);
        for (var i = 0; i < 5; i++) handler.Enqueue429(retryAfterSeconds: 0);
        var client = NewClient(handler);
        var callsBeforeAssign = 2;

        var act = async () => await client.AssignSubscriberToGroupAsync(
            "sub-1", "42", Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>(
            "exhausting bounded retries must still surface the failure");
        handler.Calls.Should().Be(callsBeforeAssign + 3,
            "retries are bounded to 3 attempts, not an infinite loop");
    }

    [HumansFact]
    public async Task AssignSubscriberToGroupAsync_ClampsAbsurdRetryAfter_ToCeiling()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueJson(HttpStatusCode.OK, """{"data":[],"meta":{"next_cursor":null}}""");
        handler.EnqueueJson(HttpStatusCode.OK, HumansGroupPage);
        handler.Enqueue429(retryAfterSeconds: 86400); // a day — clock skew / malformed header
        var logger = new CapturingLogger<MailerLiteClient>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            Xunit.TestContext.Current.CancellationToken);
        // Cancel the instant the retry warning is written, so the client reaches its
        // clamped 90s Task.Delay with an already-cancelled token. Ordering, not wall
        // clock: a loaded runner can no longer cancel before the warning exists.
        var client = NewClient(handler, new CancelOnRetryWarningLogger(logger, cts));

        var act = async () => await client.AssignSubscriberToGroupAsync("sub-1", "42", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "the clamped 90s delay is still pending when the token cancels");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains("retrying in 90s"),
            "an absurd Retry-After must clamp to the 90s ceiling, not be honored verbatim");
    }

    [HumansFact]
    public async Task AssignSubscriberToGroupAsync_NoRetryAfterHeader_WaitsTheDefault60s()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueJson(HttpStatusCode.OK, """{"data":[],"meta":{"next_cursor":null}}""");
        handler.EnqueueJson(HttpStatusCode.OK, HumansGroupPage);
        handler.Enqueue429NoRetryAfter();
        var logger = new CapturingLogger<MailerLiteClient>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            Xunit.TestContext.Current.CancellationToken);
        // Same ordering trick as the clamp test: cancel once the warning is written, so the
        // pending delay is observed through the log line rather than the wall clock.
        var client = NewClient(handler, new CancelOnRetryWarningLogger(logger, cts));

        var act = async () => await client.AssignSubscriberToGroupAsync("sub-1", "42", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "the default 60s delay is still pending when the token cancels");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains("retrying in 60s"),
            "a 429 with no Retry-After must fall back to ML's 60s rate-limit window");
    }

    private static MailerLiteClient NewClient(HttpMessageHandler handler, ILogger<MailerLiteClient>? logger = null) =>
        new(new StubHttpClientFactory(handler),
            NodaTime.SystemClock.Instance,
            logger ?? NullLogger<MailerLiteClient>.Instance);

    // Forwards everything to the capturing logger, then cancels once the 429 retry
    // warning has been recorded — the deterministic trigger the timed budget was
    // standing in for.
    private sealed class CancelOnRetryWarningLogger(
        CapturingLogger<MailerLiteClient> inner, CancellationTokenSource cts)
        : ILogger<MailerLiteClient>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            inner.Log(logLevel, eventId, state, exception, formatter);
            if (logLevel == LogLevel.Warning
                && formatter(state, exception).Contains("retrying in", StringComparison.Ordinal))
                cts.Cancel();
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public int Calls { get; private set; }

        public void EnqueueJson(HttpStatusCode status, string body)
            => _responses.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });

        public void Enqueue429(int retryAfterSeconds)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"message":"Too Many Attempts."}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
            resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));
            _responses.Enqueue(resp);
        }

        // A 429 with no Retry-After at all — ML omits it on some rate-limit responses.
        public void Enqueue429NoRetryAfter()
            => _responses.Enqueue(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"message":"Too Many Attempts."}""",
                    System.Text.Encoding.UTF8, "application/json"),
            });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
                });
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://example.test/") };
    }
}
