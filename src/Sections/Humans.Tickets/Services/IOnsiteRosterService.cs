using NodaTime;
using Humans.Base.Interfaces;

namespace Humans.Tickets.Services;

internal interface IOnsiteRosterService : IApplicationService
{
    /// <summary>Filters match names case-insensitively. Rows are unordered: the caller sorts.</summary>
    Task<OnsiteRosterResult> GetRosterAsync(
        int year,
        string? campFilter,
        string? teamFilter,
        string? roleFilter,
        CancellationToken ct = default);
}

/// <summary>The name sets drive the filter dropdowns.</summary>
internal sealed record OnsiteRosterResult(
    IReadOnlyList<OnsiteRosterRow> Rows,
    IReadOnlyList<string> AvailableCamps,
    IReadOnlyList<string> AvailableTeams,
    IReadOnlyList<string> AvailableRoles);

internal sealed record OnsiteRosterRow(
    Guid UserId,
    Instant CheckedInAt,
    IReadOnlyList<string> CampNames,
    IReadOnlyList<string> TeamNames,
    IReadOnlyList<string> RoleNames);
