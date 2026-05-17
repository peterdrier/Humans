using Humans.Domain.Enums;

namespace Humans.Application.Services.AuditLog;

/// <summary>Single source of truth for AuditAction → verb tables (transitive, self-form, description-tail policy). Shared by agent + view components.</summary>
internal static class AuditEventTextualizer
{
    /// <summary>Transitive verb for action. Null = no mapping (HTML falls back to Description; agent skips).</summary>
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

    /// <summary>Self-form verb for actor==subject case ("Peter signed up for X" vs "Peter created signup for X").</summary>
    internal static string? GetActionSelfVerb(AuditAction action) => action switch
    {
        AuditAction.ShiftSignupCreated => "signed up for",
        AuditAction.ShiftSignupConfirmed => "signed up for",
        AuditAction.ShiftSignupBailed => "bailed from",
        _ => null
    };

    /// <summary>True when Description is a context tail to append (vs a stand-alone sentence).</summary>
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

    /// <summary>Trims a dangling " for"/" to" preposition when the verb has no subject.</summary>
    internal static string TrimDanglingPreposition(string verb)
    {
        if (verb.EndsWith(" for", StringComparison.Ordinal))
            return verb[..^4];
        if (verb.EndsWith(" to", StringComparison.Ordinal))
            return verb[..^3];
        return verb;
    }
}
