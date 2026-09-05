using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.EarlyEntry.Contracts;

/// <summary>
/// Where every source of early entry is added up: earliest date wins, distinct
/// reasons listed.
/// </summary>
public interface IEarlyEntryService : IOrchestrator
{
    /// <summary>Every holder for the active event, one row each. Live on every call — never cached.</summary>
    Task<IReadOnlyList<EarlyEntryRosterRow>> GetRosterAsync(CancellationToken ct);

    /// <summary>One person's early entry, or null. Cached per person, negatives included; only eviction refreshes it.</summary>
    Task<UserEarlyEntry?> GetForUserAsync(Guid userId, CancellationToken ct);
}

public sealed record EarlyEntryRosterRow(
    Guid UserId,
    LocalDate EarliestEntryDate,
    IReadOnlyList<string> Sources,
    bool HasMultiple);

public sealed record UserEarlyEntry(LocalDate EarliestEntryDate, IReadOnlyList<string> Sources);
