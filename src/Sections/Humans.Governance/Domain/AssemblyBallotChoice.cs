namespace Humans.Governance.Domain;

/// <summary>
/// What a member recorded. <see cref="Ranked"/> means the content is in the ballot's
/// ranking; the other three are YesNo answers. Persisted as a string.
/// </summary>
internal enum AssemblyBallotChoice
{
    /// <summary>In favour.</summary>
    Yes = 0,

    /// <summary>Against.</summary>
    No = 1,

    /// <summary>Present but neither for nor against; counts toward turnout only.</summary>
    Abstain = 2,

    /// <summary>A ranked-choice ballot; the preference order is in the ranking.</summary>
    Ranked = 3
}
