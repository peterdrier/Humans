using System.Collections.Concurrent;
using Humans.Application.Interfaces.Stores;
using NodaTime;

namespace Humans.Infrastructure.Stores;

public sealed class AgentRateLimitStore : IAgentRateLimitStore
{
    private readonly ConcurrentDictionary<(Guid UserId, LocalDate Day), (int Messages, int Tokens)> _counters = new();

    public AgentRateLimitSnapshot Get(Guid userId, LocalDate day) =>
        _counters.TryGetValue((userId, day), out var v)
            ? new AgentRateLimitSnapshot(v.Messages, v.Tokens)
            : new AgentRateLimitSnapshot(0, 0);

    public void Record(Guid userId, LocalDate day, int messagesDelta, int tokensDelta) =>
        _counters.AddOrUpdate(
            (userId, day),
            addValueFactory: _ => (messagesDelta, tokensDelta),
            updateValueFactory: (_, current) => (current.Messages + messagesDelta, current.Tokens + tokensDelta));
}
