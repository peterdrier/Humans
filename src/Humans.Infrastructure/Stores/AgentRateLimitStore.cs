using System.Collections.Concurrent;
using Humans.Application.Interfaces.Stores;
using NodaTime;

namespace Humans.Infrastructure.Stores;

public sealed class AgentRateLimitStore : IAgentRateLimitStore
{
    private readonly ConcurrentDictionary<(Guid UserId, LocalDate Day), (int Messages, int Tokens)> _daily = new();
    private readonly ConcurrentDictionary<(Guid UserId, LocalDate Day, int Hour), int> _hourly = new();

    public AgentRateLimitSnapshot Get(Guid userId, LocalDate day, int hour)
    {
        var (messages, tokens) = _daily.TryGetValue((userId, day), out var d) ? d : (0, 0);
        var thisHour = _hourly.TryGetValue((userId, day, hour), out var h) ? h : 0;
        return new AgentRateLimitSnapshot(messages, tokens, thisHour);
    }

    public void Record(Guid userId, LocalDate day, int hour, int messagesDelta, int tokensDelta)
    {
        _daily.AddOrUpdate(
            (userId, day),
            addValueFactory: _ => (messagesDelta, tokensDelta),
            updateValueFactory: (_, current) => (current.Messages + messagesDelta, current.Tokens + tokensDelta));

        if (messagesDelta != 0)
        {
            _hourly.AddOrUpdate(
                (userId, day, hour),
                addValueFactory: _ => messagesDelta,
                updateValueFactory: (_, current) => current + messagesDelta);
        }
    }
}
