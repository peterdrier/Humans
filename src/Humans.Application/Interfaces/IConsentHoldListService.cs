using Humans.Domain.Entities;

namespace Humans.Application.Interfaces;

/// <summary>
/// Admin-only CRUD over the Consent Hold List. Entries on the hold list are
/// consulted by the auto Consent Check job — a name match blocks auto-approval
/// and leaves the entry in the manual review queue. See docs/features/auto-consent-check.md.
/// </summary>
public interface IConsentHoldListService
{
    /// <summary>Returns all hold-list entries ordered by most recently added first.</summary>
    Task<IReadOnlyList<ConsentHoldListEntry>> ListAsync(CancellationToken ct = default);

    /// <summary>Adds a new hold-list entry. Writes an audit log entry.</summary>
    Task<ConsentHoldListEntry> AddAsync(string entry, string? note, Guid addedByUserId, CancellationToken ct = default);

    /// <summary>Removes a hold-list entry by id. Writes an audit log entry. No-op if the entry doesn't exist.</summary>
    Task DeleteAsync(int id, Guid actingUserId, CancellationToken ct = default);
}
