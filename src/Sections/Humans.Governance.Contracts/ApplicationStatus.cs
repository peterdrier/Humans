namespace Humans.Governance.Contracts;

/// <summary>
/// Represents the status of a membership application.
/// Used with Stateless state machine for workflow management.
/// </summary>
public enum ApplicationStatus
{
    Submitted = 0,

    Approved = 2,

    Rejected = 3,

    Withdrawn = 4
}
