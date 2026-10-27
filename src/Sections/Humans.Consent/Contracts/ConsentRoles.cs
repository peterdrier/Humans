namespace Humans.Consent.Contracts;

/// <summary>Consent's grantable role. Declared to Auth by Consent's <c>Section</c>.</summary>
public static class ConsentRoles
{
    /// <summary>Consent Coordinator: performs the safety checks on new humans during onboarding (clear or flag).</summary>
    public const string ConsentCoordinator = "ConsentCoordinator";
}
