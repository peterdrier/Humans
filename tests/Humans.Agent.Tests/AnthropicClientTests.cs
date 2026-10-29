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
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public async Task StreamAsync_PreservesKnownUsage_WhenTheResponseEnds(bool hasMessageStop, bool abandon)
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
        finalizer.InputTokens.Should().Be(100);
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
