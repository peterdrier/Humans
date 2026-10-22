namespace Humans.Governance.Domain;

/// <summary>
/// What a member recorded. <see cref="Ranked"/> means the content is in the ballot's
/// ranking; the other three are YesNo answers. Persisted as a string.
/// </summary>
internal enum AssemblyBallotChoice
{
    Yes = 0,

    No = 1,

    /// <summary>Present but neither for nor against; counts toward turnout only.</summary>
    Abstain = 2,

    Ranked = 3
}
