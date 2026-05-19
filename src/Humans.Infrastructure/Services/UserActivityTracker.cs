using System.Collections.Concurrent;
using NodaTime;
using Humans.Application.Interfaces;

namespace Humans.Infrastructure.Services;

/// <inheritdoc cref="IUserActivityTracker"/>
public sealed class UserActivityTracker(IClock clock) : IUserActivityTracker
{
    private readonly ConcurrentDictionary<Guid, Instant> _lastSeen = new();

    public void Touch(Guid userId)
    {
        var now = clock.GetCurrentInstant();
        _lastSeen[userId] = now;
    }

    public int CountActiveWithin(Duration window)
    {
        var cutoff = clock.GetCurrentInstant() - window;
        var count = 0;
        foreach (var lastSeen in _lastSeen.Values)
        {
            if (lastSeen > cutoff) count++;
        }
        return count;
    }
}
