using System.Runtime.CompilerServices;
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
        if (_scripted.Count == 0)
            throw new InvalidOperationException("AnthropicClientFake has no scripted turn left.");
        foreach (var token in _scripted.Dequeue())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return token;
            await Task.Yield();
        }
    }
}
