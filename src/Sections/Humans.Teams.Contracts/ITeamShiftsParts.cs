using Humans.Base.Attributes;
using Humans.Base.Interfaces;

namespace Humans.Teams.Contracts;

/// <summary>The viewer is a signed-in member with a profile; the team is not a system team.</summary>
public sealed record TeamShiftsBoxArgs(Guid TeamId, string Slug, IReadOnlyList<Guid> ChildTeamIds, Guid ViewerUserId);

public sealed record TeamAdminRowShiftsArgs(Guid TeamId, string Slug);

/// <summary>
/// The shifts pieces of Teams' pages, filled by Shifts: a box on the team page, and the pending-signups cell in the
/// admin team list. Having no contributor is fine.
/// </summary>
public interface ITeamShiftsParts : ISectionContribution
{
    [ViewComponentSlot(typeof(TeamShiftsBoxArgs))]
    Type Box { get; }

    [ViewComponentSlot(typeof(TeamAdminRowShiftsArgs))]
    Type AdminRowCell { get; }
}
