namespace Humans.Shifts.Contracts;

/// <summary>Stored as a string.</summary>
public enum SignupStatus
{
    /// <summary>Awaiting coordinator approval on a RequireApproval rota.</summary>
    Pending = 0,

    Confirmed = 1,

    Refused = 2,

    /// <summary>The volunteer or a coordinator withdrew it.</summary>
    Bailed = 3,

    /// <summary>System-cancelled (shift removed, account deletion) or removed by a coordinator.</summary>
    Cancelled = 4,

    NoShow = 5
}
