namespace Humans.Domain.Enums;

/// <summary>
/// Identifies an external sync service.
/// </summary>
public enum SyncServiceType
{
    GoogleDrive = 0,
    GoogleGroups = 1,
    Discord = 2,
    /// <summary>
    /// LLM-powered auto-approval of the Consent Check onboarding review step.
    /// <see cref="SyncMode.None"/> means disabled; anything else means enabled.
    /// </summary>
    AutoConsentCheck = 3,
}
