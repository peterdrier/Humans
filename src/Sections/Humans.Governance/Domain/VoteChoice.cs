namespace Humans.Governance.Domain;

/// <summary>
/// Individual Board member's vote on a tier application.
/// BoardVote records are transient — deleted when the application is finalized (GDPR data minimization).
/// </summary>
internal enum VoteChoice
{
    Yay = 0,

    /// <summary>
    /// Leaning yes but has concerns.
    /// </summary>
    Maybe = 1,

    No = 2,

    Abstain = 3
}
