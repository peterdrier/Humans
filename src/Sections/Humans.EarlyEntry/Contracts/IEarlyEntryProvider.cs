using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.EarlyEntry.Contracts;

/// <summary>What this section grants for the active event; empty when it grants nothing.</summary>
public interface IEarlyEntryProvider : IFanout
{
    Task<IReadOnlyList<EarlyEntryGrant>> GetEarlyEntriesAsync(CancellationToken ct);
}

/// <summary><c>Source</c> is display copy, rendered verbatim (e.g. "Camp: Flaming Lotus").</summary>
public sealed record EarlyEntryGrant(Guid UserId, LocalDate EntryDate, string Source);
