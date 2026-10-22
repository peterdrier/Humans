using Humans.Tickets.Services;

namespace Humans.Tickets.Models;

internal sealed record OnsiteRosterViewModel(
    int Year,
    string? CampFilter,
    string? TeamFilter,
    string? RoleFilter,
    IReadOnlyList<string> AvailableCamps,
    IReadOnlyList<string> AvailableTeams,
    IReadOnlyList<string> AvailableRoles,
    IReadOnlyList<OnsiteRosterRow> Rows);
