using Humans.Tickets.Services;

namespace Humans.Tickets.Models;

/// <summary>
/// View model for <c>/Tickets/Admin/Onsite</c> — flat roster of currently
/// checked-in humans for the active event year, joined with their camp / team /
/// governance-role names. Issue nobodies-collective/Humans#736. Rows are the
/// service-layer <see cref="OnsiteRosterRow"/> records — no Web-layer remapping
/// needed since the shape is already presentation-ready.
/// </summary>
internal sealed record OnsiteRosterViewModel(
    int Year,
    string? CampFilter,
    string? TeamFilter,
    string? RoleFilter,
    IReadOnlyList<string> AvailableCamps,
    IReadOnlyList<string> AvailableTeams,
    IReadOnlyList<string> AvailableRoles,
    IReadOnlyList<OnsiteRosterRow> Rows);
