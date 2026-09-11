namespace Humans.Governance.Domain;

/// <summary>What shape of ballot an assembly vote uses. Persisted as a string.</summary>
internal enum AssemblyVoteKind
{
    /// <summary>Fixed Yes / No / Abstain ballot.</summary>
    YesNo = 0,

    /// <summary>Authored options ranked by preference, counted by instant-runoff.</summary>
    RankedChoice = 1
}
