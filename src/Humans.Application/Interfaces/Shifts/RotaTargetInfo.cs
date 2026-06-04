namespace Humans.Application.Interfaces.Shifts;

/// <summary>
/// Cross-section read projection describing a rota signup target: its id and
/// name plus the owning team's id and slug. The Shifts service resolves the
/// team slug via <c>ITeamServiceRead</c> (the stripped <c>Rota.Team</c> nav is
/// never queried). Consumed by the Camps section's shift-obligation logic.
/// </summary>
public sealed record RotaTargetInfo(Guid RotaId, string RotaName, Guid TeamId, string TeamSlug);

/// <summary>
/// Lightweight rota list item for name-based pickers: the rota id, its name, and
/// the owning team's display name (for disambiguation, e.g. "Shit-ninja (LnT)").
/// The team name is resolved at the service layer via <c>ITeamServiceRead</c>.
/// </summary>
public sealed record RotaListItem(Guid Id, string Name, string TeamName);
