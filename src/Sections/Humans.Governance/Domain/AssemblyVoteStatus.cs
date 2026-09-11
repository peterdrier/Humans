namespace Humans.Governance.Domain;

/// <summary>
/// Lifecycle of an assembly vote. <see cref="Closed"/> and <see cref="Cancelled"/> are
/// terminal — a vote never reopens; to redo one, create a new vote.
/// </summary>
internal enum AssemblyVoteStatus
{
    /// <summary>Editable and deletable by Board or Admin. No roster, no ballots.</summary>
    Draft = 0,

    /// <summary>Roster frozen, content locked, ballots accepted, tally embargoed.</summary>
    Open = 1,

    /// <summary>Result computed and stored once; ballots read-only.</summary>
    Closed = 2,

    /// <summary>Abandoned with a reason; ballots retained, no result computed.</summary>
    Cancelled = 3
}
