using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using JetBrains.Annotations;

namespace Humans.Teams.Contracts;

/// <summary>The viewer is signed in.</summary>
[UsedImplicitly(ImplicitUseKindFlags.Access, ImplicitUseTargetFlags.WithMembers)]
public sealed record TeamTasksCardArgs(Guid TeamId);

/// <summary>The tasks card on the team page, filled by Tasks. Having no contributor is fine.</summary>
public interface ITeamTasksPart : ISectionContribution
{
    [ViewComponentSlot(typeof(TeamTasksCardArgs))]
    Type Card { get; }
}
