namespace Humans.EarlyEntry.Contracts;

/// <summary>EarlyEntry's grantable role. Declared to Auth by EarlyEntry's <c>Section</c>.</summary>
public static class EarlyEntryRoles
{
    /// <summary>
    /// Early Entry Admin: grants, edits and revokes early-entry grants on any team that has early entry enabled.
    /// Coordinators manage early entry for their own team without it. Confers nothing else. Stored as
    /// <c>EETeamAdmin</c> before 2026-10.
    /// </summary>
    public const string EarlyEntryAdmin = "EarlyEntryAdmin";
}
