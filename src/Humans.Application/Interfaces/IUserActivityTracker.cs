using NodaTime;

namespace Humans.Application.Interfaces;

/// <summary>
/// Process-local "who's online" registry. Stamps the current instant against a
/// userId on every authenticated request (via middleware), so observable gauges
/// can answer "how many distinct users were active in the last N minutes/hours".
/// </summary>
/// <remarks>
/// In-memory only — state is lost on process restart. Daily container bounces
/// mean windows longer than the current uptime under-report; this is acceptable
/// for Now/1h/24h tiles, but rules out week/month windows.
/// </remarks>
public interface IUserActivityTracker
{
    /// <summary>Record that <paramref name="userId"/> was active right now.</summary>
    void Touch(Guid userId);

    /// <summary>Count distinct users with a Touch within the trailing <paramref name="window"/>.</summary>
    int CountActiveWithin(Duration window);
}
