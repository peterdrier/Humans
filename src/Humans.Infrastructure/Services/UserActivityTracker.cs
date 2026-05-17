using System.Collections.Concurrent;
using NodaTime;
using Humans.Application.Interfaces;

namespace Humans.Infrastructure.Services;

/// <inheritdoc cref="IUserActivityTracker"/>
public sealed class UserActivityTracker : IUserActivityTracker
{
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<Guid, Instant> _lastSeen = new();

    public UserActivityTracker(IClock clock)
    {
        _clock = clock;
    }

    public void Touch(Guid userId)
    {
        var now = _clock.GetCurrentInstant();
        _lastSeen[userId] = now;
    }

    public int CountActiveWithin(Duration window)
    {
        var cutoff = _clock.GetCurrentInstant() - window;
        var count = 0;
        foreach (var lastSeen in _lastSeen.Values)
        {
            if (lastSeen > cutoff) count++;
        }
        return count;
    }
}
