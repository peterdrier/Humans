namespace Humans.Governance.Domain;

/// <summary>
/// Who, beyond the official electorate, may cast an indicative (never counted)
/// ballot. Persisted as a string.
/// </summary>
internal enum IndicativeAudience
{
    /// <summary>Official roster only.</summary>
    None = 0,

    /// <summary>Active Colaboradores may also cast an indicative ballot.</summary>
    Colaboradores = 1,

    /// <summary>Colaboradores and every active Volunteer may cast an indicative ballot.</summary>
    AllMembers = 2
}
