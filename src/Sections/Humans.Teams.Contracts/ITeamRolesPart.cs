using Humans.Base.Attributes;
using Humans.Base.Interfaces;

namespace Humans.Teams.Contracts;

/// <summary>The viewer is signed in.</summary>
public sealed record TeamRolesCardArgs(Guid TeamId);

/// <summary>The roles card on the team page, filled by Roles. Having no contributor is fine.</summary>
public interface ITeamRolesPart : ISectionContribution
{
    [ViewComponentSlot(typeof(TeamRolesCardArgs))]
    Type Card { get; }
}
