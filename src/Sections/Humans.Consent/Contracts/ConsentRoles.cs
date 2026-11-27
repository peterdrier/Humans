namespace Humans.Consent.Contracts;

/// <summary>Consent's grantable role. Declared to Auth by Consent's <c>Section</c>.</summary>
public static class ConsentRoles
{
    /// <summary>Consent Admin: works the consent review queue (clear or flag new humans). Stored as <c>ConsentCoordinator</c> before 2026-11.</summary>
    public const string ConsentAdmin = "ConsentAdmin";
}
