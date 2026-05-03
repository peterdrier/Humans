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
        // Evict buckets older than yesterday. Bounded process lifetime is no longer
        // a guarantee at this scale of activity, so the dictionaries can't be
        // allowed to grow unboundedly across never-returning users. Yesterday is
        // retained for late-arriving turns spanning a midnight rollover.
        var staleBefore = day.PlusDays(-1);
        foreach (var key in _daily.Keys)
            if (key.Day < staleBefore) _daily.TryRemove(key, out _);
        foreach (var key in _hourly.Keys)
            if (key.Day < staleBefore) _hourly.TryRemove(key, out _);

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
