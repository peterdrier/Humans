using NodaTime;

namespace Humans.Application.Interfaces.Tickets;

/// <summary>
/// Orchestrates the "Who's onsite" view (#736). Joins the cached set of
/// Attended+CheckedInAt humans for the active year with their camp / team /
/// governance-role names and applies the requested filters. Used by the
/// <c>/Tickets/Admin/Onsite</c> admin view.
/// </summary>
public interface IOnsiteRosterService : IApplicationService
{
    /// <summary>
    /// Returns the filtered, enriched onsite roster for <paramref name="year"/>.
    /// Filter strings match camp / team / governance-role names case-insensitively
    /// (Ordinal). Rows are returned in unspecified order — caller sorts at the
    /// presentation layer per <c>memory/architecture/display-sort-in-controllers.md</c>.
    /// </summary>
    Task<OnsiteRosterResult> GetRosterAsync(
        int year,
        string? campFilter,
        string? teamFilter,
        string? roleFilter,
        CancellationToken ct = default);
}

/// <summary>
/// Outcome of <see cref="IOnsiteRosterService.GetRosterAsync"/>: the filtered
/// rows plus the full set of camp / team / role names known to be onsite
/// (drives the filter dropdowns in the view).
/// </summary>
public sealed record OnsiteRosterResult(
    IReadOnlyList<OnsiteRosterRow> Rows,
    IReadOnlyList<string> AvailableCamps,
    IReadOnlyList<string> AvailableTeams,
    IReadOnlyList<string> AvailableRoles);

public sealed record OnsiteRosterRow(
    Guid UserId,
    Instant CheckedInAt,
    IReadOnlyList<string> CampNames,
    IReadOnlyList<string> TeamNames,
    IReadOnlyList<string> RoleNames);
