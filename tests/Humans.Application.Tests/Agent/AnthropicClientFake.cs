using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Humans.Application.Interfaces;
using Humans.Application.Models;

namespace Humans.Application.Tests.Agent;

internal sealed class AnthropicClientFake : IAnthropicClient
{
    private readonly Queue<IReadOnlyList<AgentTurnToken>> _scripted = new();

    public AnthropicRequest? LastRequest { get; private set; }

    public void EnqueueTurn(params AgentTurnToken[] tokens) => _scripted.Enqueue(tokens);

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
