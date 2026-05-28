using Humans.Application.Interfaces;
using NodaTime;

namespace Humans.Application.Interfaces.EarlyEntry;

/// <summary>
/// Cross-source EE read orchestrator. Fans out over every
/// <see cref="IEarlyEntryProvider"/> and assembles per-user results. Owns no
/// tables (orchestrator per the hard rules).
/// </summary>
public interface IEarlyEntryService : IOrchestrator
{
    /// <summary>All EE holders for the active event, one row per user, with the
    /// per-source breakdown and a wasted-slot flag. Live (uncached).</summary>
    Task<IReadOnlyList<EarlyEntryRosterRow>> GetRosterAsync(CancellationToken ct);

    /// <summary>The viewer's own EE, or null if they hold none. Cached.</summary>
    Task<UserEarlyEntry?> GetForUserAsync(Guid userId, CancellationToken ct);
}

/// <summary>One roster row: a user and every source that grants them EE.</summary>
public sealed record EarlyEntryRosterRow(
    Guid UserId,
    LocalDate EarliestEntryDate,
    IReadOnlyList<string> Sources,
    bool HasMultiple);

/// <summary>The earliest date a user may enter, plus the source label(s).</summary>
public sealed record UserEarlyEntry(LocalDate EarliestEntryDate, IReadOnlyList<string> Sources);
