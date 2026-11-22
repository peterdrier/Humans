using Humans.Base.Interfaces;

namespace Humans.Users.Contracts;

/// <summary>
/// A section that needs some of a human's addresses kept on their account. Users refuses to delete a locked
/// address and the Emails grid hides its Delete action.
/// </summary>
public interface IUserEmailLock : IFanout
{
    /// <summary>Read-only. Empty when this section locks nothing for the human.</summary>
    Task<IReadOnlyCollection<string>> GetLockedAddressesAsync(Guid userId, CancellationToken ct = default);
}
