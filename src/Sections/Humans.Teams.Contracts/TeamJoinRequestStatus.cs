namespace Humans.Teams.Contracts;

public enum TeamJoinRequestStatus
{
    Pending = 0,

    Approved = 1,

    Rejected = 2,

    /// <summary>By the applicant.</summary>
    Withdrawn = 3
}
