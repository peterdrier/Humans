using Humans.Base.Interfaces;

namespace Humans.Grants.Contracts;

/// <summary>Reads for sections that deliver what a grant entitles (Tickets prices a ticket from them).</summary>
public interface IGrantService : IApplicationService
{
    /// <summary>The person's grants that are active today, across every id they have held.</summary>
    Task<IReadOnlyList<ActiveGrantInfo>> GetActiveGrantsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Every program, newest event first, for a picker that maps a program to what it delivers.</summary>
    Task<IReadOnlyList<GrantProgramSummary>> GetProgramSummariesAsync(CancellationToken ct = default);
}

/// <summary><paramref name="IssuedAt"/> is when the grant was given: a low income price pins the wave on sale then.</summary>
public sealed record ActiveGrantInfo(Guid GrantId, Guid ProgramId, string ProgramName, Guid? EventSettingsId, NodaTime.Instant IssuedAt);

public sealed record GrantProgramSummary(Guid ProgramId, string Name, int Year, Guid? EventSettingsId, string EventName);
