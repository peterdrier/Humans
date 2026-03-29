namespace Humans.Domain.Enums;

/// <summary>
/// State machine for duty signup lifecycle.
/// Stored as string in DB.
/// </summary>
public enum SignupStatus
{
    /// <summary>Awaiting coordinator approval (RequireApproval policy).</summary>
    Pending = 0,

    /// <summary>Volunteer is confirmed for the shift.</summary>
    Confirmed = 1,

    /// <summary>Coordinator refused the signup request.</summary>
    Refused = 2,

    /// <summary>Volunteer or coordinator withdrew from confirmed/pending signup.</summary>
    Bailed = 3,

    /// <summary>System-cancelled (e.g., shift deactivated, account deletion).</summary>
    Cancelled = 4,

    /// <summary>Volunteer did not show up for a confirmed shift.</summary>
    NoShow = 5
}
