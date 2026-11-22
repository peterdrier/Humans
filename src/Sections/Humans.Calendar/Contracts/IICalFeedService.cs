using Humans.Base.Interfaces;

namespace Humans.Calendar.Contracts;

public interface IICalFeedService : IOrchestrator
{
    /// <summary>Contributed commitments. No token check: callers are already authorized. Empty for an unknown user.</summary>
    Task<IReadOnlyList<CalendarFeedItem>> GetFeedItemsAsync(Guid userId, CancellationToken ct = default);

}
