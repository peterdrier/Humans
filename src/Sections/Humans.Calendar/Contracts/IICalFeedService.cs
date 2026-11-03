using Humans.Base.Interfaces;

namespace Humans.Calendar.Contracts;

public interface IICalFeedService : IOrchestrator
{
    /// <summary>Contributed commitments. No token check: callers are already authorized. Empty for an unknown user.</summary>
    Task<IReadOnlyList<CalendarFeedItem>> GetFeedItemsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Null when the user is missing or merged or the token is wrong: one 404 for all, so no oracle.
    /// </summary>
    Task<string?> GetFeedIcsAsync(Guid userId, Guid token, CancellationToken ct = default);

    /// <summary>Never yields the token itself.</summary>
    Task<bool> HasFeedAsync(Guid userId, CancellationToken ct = default);
}
