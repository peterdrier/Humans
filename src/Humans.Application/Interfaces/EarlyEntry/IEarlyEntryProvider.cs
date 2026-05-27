using NodaTime;

namespace Humans.Application.Interfaces.EarlyEntry;

/// <summary>
/// Contributes the Early Entry grants this section owns for the active event.
/// Mirrors the GDPR <c>IUserDataContributor</c> fan-out: each section that owns
/// EE-relevant data implements this; <see cref="IEarlyEntryService"/> assembles
/// the cross-source view. Read-only. A section with nothing to contribute
/// (e.g. no EE start date configured) returns an empty list.
/// </summary>
public interface IEarlyEntryProvider : IFanout
{
    Task<IReadOnlyList<EarlyEntryGrant>> GetEarlyEntriesAsync(CancellationToken ct);
}

/// <summary>
/// One EE grant: the user, the date they may enter, and a display label for the
/// source (e.g. "Camp: Flaming Lotus", "Shift: Flags").
/// </summary>
public sealed record EarlyEntryGrant(Guid UserId, LocalDate EntryDate, string Source);
