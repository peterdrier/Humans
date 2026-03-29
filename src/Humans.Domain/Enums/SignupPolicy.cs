namespace Humans.Domain.Enums;

/// <summary>
/// Controls whether shift signups require coordinator approval.
/// Stored as string in DB.
/// </summary>
public enum SignupPolicy
{
    /// <summary>Signups are auto-confirmed.</summary>
    Public = 0,

    /// <summary>Signups require coordinator approval.</summary>
    RequireApproval = 1
}
