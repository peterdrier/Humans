using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.EarlyEntry.Contracts;

/// <summary>Every source of early entry added up: earliest date wins, distinct sources listed.</summary>
public interface IEarlyEntryService : IOrchestrator
{
    /// <summary>Every holder for the active event, one row each. Gathered once per request scope.</summary>
    Task<IReadOnlyList<EarlyEntryRosterRow>> GetRosterAsync(CancellationToken ct);

    /// <summary>Null when the member holds no early entry. Gathered once per request scope.</summary>
    Task<EarlyEntryRosterRow?> GetForUserAsync(Guid userId, CancellationToken ct);
}

public sealed record EarlyEntryRosterRow(
    Guid UserId,
    LocalDate EarliestEntryDate,
    IReadOnlyList<string> Sources,
    bool HasMultiple);
