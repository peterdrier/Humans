using Humans.Domain.Enums;

namespace Humans.Application.Services.AuditLog;

/// <summary>
/// Single source of truth for audit-event textualization. Owns the verb tables
/// (transitive verb, self-form verb, description-tail policy) that translate
/// an <see cref="AuditAction"/> into human-readable English. Used by
/// <see cref="AuditEvent.RenderPlainText"/> (agent / log lines) and
/// <see cref="AuditEvent.RenderStructured"/> (HTML composition by view
/// components). Keeping the tables in one place is what lets the agent's
/// plain-text output and the view component's HTML stay in lock-step.
/// </summary>
internal static class AuditEventTextualizer
{
    /// <summary>
    /// Maps an <see cref="AuditAction"/> to a short transitive verb phrase. Returns
    /// <c>null</c> when the action has no structured verb mapping — callers
    /// fall back to <see cref="AuditLogEntry.Description"/> (HTML) or skip
    /// the line entirely (agent tool output, which prefers silence over
    /// dumping unstructured descriptions).
    /// </summary>
    internal static string? GetActionVerb(AuditAction action) => action switch
    {
        AuditAction.TeamMemberAdded => "added",
        AuditAction.TeamMemberRemoved => "removed",
        AuditAction.TeamMemberRoleChanged => "changed role for",
        AuditAction.TeamJoinedDirectly => "joined",
        AuditAction.TeamLeft => "left",
        AuditAction.TeamJoinRequestApproved => "approved join request for",
        AuditAction.TeamJoinRequestRejected => "rejected join request for",
        AuditAction.MemberSuspended => "suspended",
        AuditAction.MemberUnsuspended => "unsuspended",
        AuditAction.VolunteerApproved => "approved",
        AuditAction.RoleAssigned => "assigned role to",
        AuditAction.RoleEnded => "ended role for",
        AuditAction.WorkspaceAccountPasswordReset => "reset Workspace password for",
        AuditAction.WorkspaceAccountBackupCodesGenerated => "generated Workspace backup codes for",
        AuditAction.ConsentCheckCleared => "cleared consent check for",
        AuditAction.ConsentCheckFlagged => "flagged consent check for",
        AuditAction.SignupRejected => "rejected signup for",
        AuditAction.TierApplicationApproved => "approved tier application for",
        AuditAction.TierApplicationRejected => "rejected tier application for",
        AuditAction.ShiftSignupCreated => "created signup for",
        AuditAction.ShiftSignupConfirmed => "confirmed signup for",
        AuditAction.ShiftSignupRefused => "refused signup for",
        AuditAction.ShiftSignupVoluntold => "voluntold",
        AuditAction.ShiftSignupBailed => "bailed",
        AuditAction.ShiftSignupNoShow => "marked no-show for",
        AuditAction.ShiftSignupCancelled => "removed signup for",
        AuditAction.ShiftSignupReassigned => "reassigned shift signups for",
        _ => null
    };

    /// <summary>
    /// Self-form verb — used when actor and subject are the same human, so
    /// the rendered sentence skips the subject and avoids a dangling
    /// preposition (e.g. "Peter signed up for shift X" not "Peter created
    /// signup for shift X").
    /// </summary>
    internal static string? GetActionSelfVerb(AuditAction action) => action switch
    {
        AuditAction.ShiftSignupCreated => "signed up for",
        AuditAction.ShiftSignupConfirmed => "signed up for",
        AuditAction.ShiftSignupBailed => "bailed from",
        _ => null
    };

    /// <summary>
    /// True when the action's <see cref="AuditLogEntry.Description"/> is
    /// written as a context tail (e.g. "shift 'Cantina dinner @ 18:00'") and
    /// should be appended after the structured verb+subject. False when the
    /// description is a stand-alone sentence ("Joined Build Team directly")
    /// that would render redundantly after the verb.
    /// </summary>
    internal static bool ShouldRenderDescriptionTail(AuditAction action) => action
        is AuditAction.ShiftSignupCreated
        or AuditAction.ShiftSignupConfirmed
        or AuditAction.ShiftSignupRefused
        or AuditAction.ShiftSignupVoluntold
        or AuditAction.ShiftSignupBailed
        or AuditAction.ShiftSignupNoShow
        or AuditAction.ShiftSignupCancelled
        or AuditAction.ShiftSignupReassigned
        or AuditAction.RoleAssigned
        or AuditAction.RoleEnded
        or AuditAction.WorkspaceAccountPasswordReset
        or AuditAction.WorkspaceAccountBackupCodesGenerated;

    /// <summary>
    /// Trims a dangling " for"/" to" preposition when the verb has no subject
    /// to attach to (subject suppressed because actor==subject, or unknown).
    /// </summary>
    internal static string TrimDanglingPreposition(string verb)
    {
        if (verb.EndsWith(" for", StringComparison.Ordinal))
            return verb[..^4];
        if (verb.EndsWith(" to", StringComparison.Ordinal))
            return verb[..^3];
        return verb;
    }
}
