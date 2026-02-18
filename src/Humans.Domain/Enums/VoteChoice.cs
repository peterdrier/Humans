namespace Humans.Domain.Enums;

/// <summary>
/// Individual Board member's vote on a tier application.
/// BoardVote records are transient — deleted when the application is finalized (GDPR data minimization).
/// </summary>
public enum VoteChoice
{
    /// <summary>
    /// In favor of approving the application.
    /// </summary>
    Yay = 0,

    /// <summary>
    /// Leaning yes but has concerns.
    /// </summary>
    Maybe = 1,

    /// <summary>
    /// Against approving the application.
    /// </summary>
    No = 2,

    /// <summary>
    /// No position on this application.
    /// </summary>
    Abstain = 3
}
