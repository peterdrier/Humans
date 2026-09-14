namespace Humans.Governance.Domain;

/// <summary>
/// How far individual ballots are disclosed after close. Defaults to
/// <see cref="BoardOnly"/>; never disclosed beyond the roster. Persisted as a string.
/// </summary>
internal enum BallotDisclosure
{
    /// <summary>Board and Admin only, and every such view is audited.</summary>
    BoardOnly = 0,

    /// <summary>Roster members additionally see the per-member choices on the results page.</summary>
    RosterSeesNames = 1
}
