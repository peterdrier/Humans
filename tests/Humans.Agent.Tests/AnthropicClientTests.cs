using System.Net;
using System.Text;
using AwesomeAssertions;
using global::Anthropic.Core;
using Humans.Agent.Models;
using Humans.Agent.Services.Anthropic;
using Microsoft.Extensions.Logging.Abstractions;
using ProviderClient = global::Anthropic.AnthropicClient;
using ClientAdapter = Humans.Agent.Services.Anthropic.AnthropicClient;

namespace Humans.Agent.Tests;

public sealed class AnthropicClientTests
{
    [HumansTheory]
    [Xunit.InlineData(false, false, false)]
    [Xunit.InlineData(true, false, false)]
    [Xunit.InlineData(true, true, false)]
    [Xunit.InlineData(false, false, true)]
    [Xunit.InlineData(true, false, true)]
    public async Task StreamAsync_PreservesKnownUsage_WhenTheResponseEnds(bool hasMessageStop, bool abandon, bool updatesInputUsage)
    {
        var body = """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_test","type":"message","role":"assistant","content":[],"model":"test-model","stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":100,"output_tokens":1,"cache_read_input_tokens":20,"cache_creation_input_tokens":10}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Partial answer."}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":11}}

            """ + "\n\n";
        if (updatesInputUsage)
        {
            body = body.Replace("\"usage\":{\"output_tokens\":11}",
                "\"usage\":{\"input_tokens\":150,\"output_tokens\":11}", StringComparison.Ordinal);
            // Later deltas may omit input usage; they must retain the updated count.
            body += "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":11}}\n\n";
        }
        if (hasMessageStop)
            body += "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
        using var http = new HttpClient(new StreamResponseHandler(body));
        var provider = new ProviderClient(new ClientOptions
        {
            ApiKey = "test-key",
            HttpClient = http,
            MaxRetries = 0,
        });
        var adapter = new ClientAdapter(provider, NullLogger<ClientAdapter>.Instance);
        var request = new AnthropicRequest("test-model", "System prompt",
            [new AnthropicMessage("user", "Question", null, null)], [], 100);
        var tokens = new List<AgentTurnToken>();

        await foreach (var token in adapter.StreamAsync(request, Xunit.TestContext.Current.CancellationToken))
        {
            tokens.Add(token);
            if (abandon && token.TextDelta is not null) break;
        }

        if (abandon)
        {
            tokens.Should().NotContain(t => t.Finalizer != null);
            var known = tokens.Where(t => t.Usage != null).Should().ContainSingle().Which.Usage!;
            known.InputTokens.Should().Be(100);
            known.OutputTokens.Should().Be(1);
            known.CacheReadTokens.Should().Be(20);
            known.CacheCreationTokens.Should().Be(10);
            return;
        }

        string.Concat(tokens.Select(token => token.TextDelta)).Should().Be("Partial answer.");
        var finalizer = tokens.Where(token => token.Finalizer is not null).Should().ContainSingle().Which.Finalizer!;
        finalizer.StopReason.Should().Be(hasMessageStop ? "end_turn" : "error");
        finalizer.InputTokens.Should().Be(updatesInputUsage ? 150 : 100);
        tokens.Last(token => token.Usage is not null).Usage!.InputTokens.Should().Be(finalizer.InputTokens);
        finalizer.OutputTokens.Should().Be(11);
        finalizer.CacheReadTokens.Should().Be(20);
        finalizer.CacheCreationTokens.Should().Be(10);
    }

    [HumansFact]
    public async Task A_tool_use_turn_with_thinking_is_replayed_block_for_block()
    {
        // Claude 5 models think by default; the API rejects a replayed tool-use turn that drops its thinking blocks.
        var body = """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_test","type":"message","role":"assistant","content":[],"model":"claude-sonnet-5-5","stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Look it up."}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig-1"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Checking."}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":1}

            event: content_block_start
            data: {"type":"content_block_start","index":2,"content_block":{"type":"redacted_thinking","data":"opaque"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":2}

            event: content_block_start
            data: {"type":"content_block_start","index":3,"content_block":{"type":"tool_use","id":"toolu_1","name":"fetch_section_guide","input":{}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":3,"delta":{"type":"input_json_delta","partial_json":"{\"section\":\"Teams\"}"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":3}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":20}}

            event: message_stop
            data: {"type":"message_stop"}

            """ + "\n\n";
        var handler = new StreamResponseHandler(body);
        using var http = new HttpClient(handler);
        var adapter = new ClientAdapter(
            new ProviderClient(new ClientOptions { ApiKey = "test-key", HttpClient = http, MaxRetries = 0 }),
            NullLogger<ClientAdapter>.Instance);
        var ct = Xunit.TestContext.Current.CancellationToken;
        var question = new AnthropicMessage("user", "Question", null, null);

        var tokens = new List<AgentTurnToken>();
        await foreach (var token in adapter.StreamAsync(new AnthropicRequest("claude-sonnet-5-5", "System", [question], [], 100), ct))
            tokens.Add(token);

        var content = tokens.Single(t => t.Finalizer is not null).AssistantContent;
        content.Should().Equal(
            new AnthropicThinkingBlock("Look it up.", "sig-1"),
            new AnthropicTextBlock("Checking."),
            new AnthropicRedactedThinkingBlock("opaque"),
            new AnthropicToolUseBlock(new AnthropicToolCall("toolu_1", "fetch_section_guide", "{\"section\":\"Teams\"}")));

        var replay = new AnthropicMessage("assistant", "ignored", null, null, AssistantContent: content);
        await foreach (var _ in adapter.StreamAsync(new AnthropicRequest("claude-sonnet-5-5", "System", [question, replay], [], 100), ct))
        {
        }

        using var sent = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!);
        var blocks = sent.RootElement.GetProperty("messages")[1].GetProperty("content");
        blocks.EnumerateArray().Select(b => b.GetProperty("type").GetString())
            .Should().Equal("thinking", "text", "redacted_thinking", "tool_use");
        blocks[0].GetProperty("signature").GetString().Should().Be("sig-1");
        blocks[2].GetProperty("data").GetString().Should().Be("opaque");
        blocks[3].GetProperty("input").GetProperty("section").GetString().Should().Be("Teams");
    }

    [HumansTheory]
    [Xunit.InlineData("api")]
    [Xunit.InlineData("sse")]
    [Xunit.InlineData("transport")]
    [Xunit.InlineData("unexpected")]
    public async Task StreamAsync_ClassifiesFailuresAndKeepsTheErrorFinalizer(string failureKind)
    {
        Exception? failure = failureKind switch
        {
            "transport" => new HttpRequestException("Provider unavailable"),
            "unexpected" => new InvalidOperationException("Unexpected handler failure"),
            _ => null,
        };
        using var http = new HttpClient(new FailedResponseHandler(failureKind, failure));
        var provider = new ProviderClient(new ClientOptions { ApiKey = "test-key", HttpClient = http, MaxRetries = 0 });
        var logger = new Humans.Testing.CapturingLogger<ClientAdapter>();
        var adapter = new ClientAdapter(provider, logger);
        var tokens = new List<AgentTurnToken>();

        await foreach (var token in adapter.StreamAsync(
            new AnthropicRequest("test-model", "System", [new AnthropicMessage("user", "Question", null, null)], [], 100),
            Xunit.TestContext.Current.CancellationToken))
            tokens.Add(token);

        tokens.Should().ContainSingle(token => token.Finalizer != null);
        tokens.Single(token => token.Finalizer != null).Finalizer!.StopReason.Should().Be("error");
        var entry = logger.Entries.Where(entry => entry.Level >= Microsoft.Extensions.Logging.LogLevel.Warning)
            .Should().ContainSingle().Subject;
        entry.Level.Should().Be(failureKind is "unexpected"
            ? Microsoft.Extensions.Logging.LogLevel.Error : Microsoft.Extensions.Logging.LogLevel.Warning);
        if (failureKind is "unexpected") entry.Exception.Should().BeSameAs(failure);
        else entry.Exception.Should().BeNull();
        entry.Message.Should().Contain(failureKind is "unexpected" ? "failed" : "Provider unavailable");
    }

    [HumansFact]
    public async Task StreamAsync_PropagatesCallerCancellationWithoutAnErrorFinalizer()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var http = new HttpClient(new StreamResponseHandler(""));
        var provider = new ProviderClient(new ClientOptions { ApiKey = "test-key", HttpClient = http, MaxRetries = 0 });
        var logger = new Humans.Testing.CapturingLogger<ClientAdapter>();
        var adapter = new ClientAdapter(provider, logger);
        var tokens = new List<AgentTurnToken>();

        var read = async () =>
        {
            await foreach (var token in adapter.StreamAsync(
                new AnthropicRequest("test-model", "System", [new AnthropicMessage("user", "Question", null, null)], [], 100),
                cancellation.Token))
                tokens.Add(token);
        };

        await read.Should().ThrowAsync<OperationCanceledException>();
        tokens.Should().NotContain(token => token.Finalizer != null);
        logger.Entries.Should().NotContain(entry => entry.Level >= Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [HumansTheory]
    [Xunit.InlineData("complete")]
    [Xunit.InlineData("failure")]
    [Xunit.InlineData("cancelled")]
    public async Task ListModelsAsync_ReadsEveryPageOrPropagatesTheLaterFailure(string outcome)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Xunit.TestContext.Current.CancellationToken);
        var handler = new ModelPagesHandler(outcome, cancellation);
        using var http = new HttpClient(handler);
        var provider = new ProviderClient(new ClientOptions { ApiKey = "test-key", HttpClient = http, MaxRetries = 0 });
        var adapter = new ClientAdapter(provider, NullLogger<ClientAdapter>.Instance);
        var read = () => adapter.ListModelsAsync(cancellation.Token);

        if (outcome is "failure")
            await read.Should().ThrowAsync<HttpRequestException>();
        else if (outcome is "cancelled")
            await read.Should().ThrowAsync<OperationCanceledException>();
        else
        {
            var models = await read();
            models.Select(model => (model.Id, model.DisplayName)).Should()
                .Equal(("model-new", "New model"), ("model-old", "Old model"));
        }

        handler.Queries.Should().HaveCount(2);
        handler.Queries[0].Should().Contain("limit=100");
        handler.Queries[1].Should().Contain("after_id=model-new");
    }

    [HumansTheory]
    [Xunit.InlineData(false, "api")]
    [Xunit.InlineData(true, "api")]
    [Xunit.InlineData(false, "transport")]
    [Xunit.InlineData(true, "transport")]
    [Xunit.InlineData(false, "unexpected")]
    [Xunit.InlineData(true, "unexpected")]
    public async Task Optional_reads_normalize_provider_failures_without_hiding_unexpected_errors(bool models, string kind)
    {
        Exception? failure = kind switch
        {
            "transport" => new HttpRequestException("Provider unavailable"),
            "unexpected" => new InvalidOperationException("Unexpected handler failure"),
            _ => null,
        };
        using var http = new HttpClient(new FailedResponseHandler(kind, failure));
        var provider = new ProviderClient(new ClientOptions { ApiKey = "test-key", HttpClient = http, MaxRetries = 0 });
        var adapter = new ClientAdapter(provider, NullLogger<ClientAdapter>.Instance);
        var ct = Xunit.TestContext.Current.CancellationToken;
        Task ReadAsync() => models ? adapter.ListModelsAsync(ct) : adapter.CountTokensAsync("test-model", "Text", ct);
        var read = ReadAsync;

        if (kind is "api") await read.Should().ThrowAsync<HttpRequestException>().WithMessage("*Provider unavailable*");
        else if (kind is "transport") await read.Should().ThrowAsync<IOException>().WithMessage("*Provider unavailable*");
        else (await read.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
    }

    private sealed class ModelPagesHandler(string outcome, CancellationTokenSource cancellation) : HttpMessageHandler
    {
        public List<string> Queries { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Queries.Add(request.RequestUri!.Query);
            var first = Queries.Count == 1;
            if (!first && outcome is "cancelled")
            {
                await cancellation.CancelAsync();
                return await Task.FromCanceled<HttpResponseMessage>(cancellationToken);
            }
            var failed = !first && outcome is "failure";
            var body = failed ? "{\"type\":\"error\",\"error\":{\"type\":\"api_error\",\"message\":\"Later page unavailable\"}}"
                : first
                    ? "{\"data\":[{\"id\":\"model-new\",\"display_name\":\"New model\",\"created_at\":\"2026-01-02T00:00:00Z\",\"type\":\"model\"}],\"has_more\":true,\"first_id\":\"model-new\",\"last_id\":\"model-new\"}"
                    : "{\"data\":[{\"id\":\"model-old\",\"display_name\":\"Old model\",\"created_at\":\"2026-01-01T00:00:00Z\",\"type\":\"model\"}],\"has_more\":false,\"first_id\":\"model-old\",\"last_id\":\"model-old\"}";
            return new HttpResponseMessage(failed ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class FailedResponseHandler(string kind, Exception? failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (failure is not null) return Task.FromException<HttpResponseMessage>(failure);
            return Task.FromResult(new HttpResponseMessage(kind is "api" ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(kind is "api"
                    ? "{\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\",\"message\":\"Provider unavailable\"}}"
                    : "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Provider unavailable\"}}\n\n",
                    Encoding.UTF8, kind is "api" ? "application/json" : "text/event-stream"),
            });
        }
    }

    private sealed class StreamResponseHandler(string body) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
