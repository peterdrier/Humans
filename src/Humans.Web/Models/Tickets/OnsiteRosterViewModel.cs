using NodaTime;

namespace Humans.Web.Models.Tickets;

/// <summary>
/// View model for <c>/Tickets/Admin/Onsite</c> — flat roster of currently
/// checked-in humans for the active event year, joined with their camp / team /
/// governance-role names. Issue nobodies-collective/Humans#736.
/// </summary>
public sealed record OnsiteRosterViewModel(
    int Year,
    string? CampFilter,
    string? TeamFilter,
    string? RoleFilter,
    IReadOnlyList<string> AvailableCamps,
    IReadOnlyList<string> AvailableTeams,
    IReadOnlyList<string> AvailableRoles,
    IReadOnlyList<OnsiteRosterRow> Rows);

public sealed record OnsiteRosterRow(
    Guid UserId,
    string DisplayName,
    Instant CheckedInAt,
    IReadOnlyList<string> CampNames,
    IReadOnlyList<string> TeamNames,
    IReadOnlyList<string> RoleNames);
