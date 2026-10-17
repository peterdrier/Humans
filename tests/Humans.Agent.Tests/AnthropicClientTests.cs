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
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task StreamAsync_PreservesKnownUsage_WhenTheResponseEnds(bool hasMessageStop)
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
            ApiKey = "test-key", HttpClient = http, MaxRetries = 0,
        });
        var adapter = new ClientAdapter(provider, NullLogger<ClientAdapter>.Instance);
        var request = new AnthropicRequest("test-model", "System prompt",
            [new AnthropicMessage("user", "Question", null, null)], [], 100);
        var tokens = new List<AgentTurnToken>();

        await foreach (var token in adapter.StreamAsync(request, Xunit.TestContext.Current.CancellationToken))
            tokens.Add(token);

        string.Concat(tokens.Select(token => token.TextDelta)).Should().Be("Partial answer.");
        var finalizer = tokens.Where(token => token.Finalizer is not null).Should().ContainSingle().Which.Finalizer!;
        finalizer.StopReason.Should().Be(hasMessageStop ? "end_turn" : "error");
        finalizer.InputTokens.Should().Be(100);
        finalizer.OutputTokens.Should().Be(11);
        finalizer.CacheReadTokens.Should().Be(20);
        finalizer.CacheCreationTokens.Should().Be(10);
    }

    private sealed class StreamResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            });
    }
}
