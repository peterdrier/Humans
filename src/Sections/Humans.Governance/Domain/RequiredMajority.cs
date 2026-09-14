namespace Humans.Governance.Domain;

/// <summary>
/// The majority an assembly vote needs to pass. Statutes Art. 10.2 (simple) and
/// Art. 10.4 / Art. 10.5 / Art. 33 (qualified). Persisted as a string.
/// </summary>
internal enum RequiredMajority
{
    /// <summary>More for than against; abstentions excluded.</summary>
    Simple = 0,

    /// <summary>Two thirds of the for-plus-against ballots.</summary>
    TwoThirds = 1
}
