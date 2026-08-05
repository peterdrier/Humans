using System.Runtime.CompilerServices;
using System.Text.Json;
using Humans.Application.Interfaces;
using Humans.Application.Models;

namespace Humans.Application.Tests.Agent;

internal sealed class AnthropicClientFake : IAnthropicClient
{
    private readonly Queue<IReadOnlyList<AgentTurnToken>> _scripted = new();

    public AnthropicRequest? LastRequest { get; private set; }

    /// <summary>Value returned by <see cref="CountTokensAsync"/>; set <see cref="CountTokensThrows"/> to simulate an API failure.</summary>
    public int CountTokensResult { get; set; } = 1234;
    public bool CountTokensThrows { get; set; }

    public void EnqueueTurn(params AgentTurnToken[] tokens) => _scripted.Enqueue(tokens);

    public Task<int> CountTokensAsync(string model, string text, CancellationToken cancellationToken = default) =>
        CountTokensThrows
            ? throw new InvalidOperationException("simulated count_tokens failure")
            : Task.FromResult(CountTokensResult);

    public async IAsyncEnumerable<AgentTurnToken> StreamAsync(
        AnthropicRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        RejectUnmappableToolCalls(request);
        if (_scripted.Count == 0)
            throw new InvalidOperationException("AnthropicClientFake has no scripted turn left.");
        foreach (var token in _scripted.Dequeue())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return token;
            await Task.Yield();
        }
    }

    /// <summary>
    /// Mirrors the one way the real client can reject a request the fake would
    /// otherwise wave through: <c>AnthropicClient.MapMessages</c> deserializes every
    /// replayed <c>tool_use</c> block's arguments into
    /// <c>Dictionary&lt;string, JsonElement&gt;</c>, so a malformed or non-object
    /// payload throws while the request is being built.
    /// </summary>
    /// <remarks>
    /// Without this, a test can replay a truncated tool call (as a max_tokens cutoff
    /// produces — nobodies-collective/Humans#963) and pass green while the same path
    /// would throw in production. Keep the fake's contract as strict as the client's.
    /// </remarks>
    private static void RejectUnmappableToolCalls(AnthropicRequest request)
    {
        foreach (var call in request.Messages.SelectMany(m => m.ToolCalls ?? []))
        {
            var input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(call.JsonArguments);
            if (input is null)
                throw new JsonException(
                    $"Tool call '{call.Name}' has null arguments; the real client cannot map it.");
        }
    }
}
