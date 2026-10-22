using Humans.Base.Interfaces;

namespace Humans.Email.Contracts;

/// <summary>
/// A section that holds a reason a human must keep a category as it is. The member and guest preference
/// toggles refuse to change a locked category, and both pages render it read-only. Unsubscribe links and
/// MailerLite's writes are not gated.
/// </summary>
public interface ICommunicationPreferenceLock : IFanout
{
    /// <summary>Read-only. Empty when this section locks nothing for the human.</summary>
    Task<IReadOnlyCollection<MessageCategory>> GetLockedCategoriesAsync(Guid userId, CancellationToken ct = default);
}
