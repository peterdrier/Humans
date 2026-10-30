using Humans.AuditLog.Contracts;

namespace Humans.Governance;

/// <summary>The audit actions Governance writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class GovernanceAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition TierApplicationApproved = new("TierApplicationApproved", Verb: "approved tier application for");

    public static readonly AuditActionDefinition TierApplicationRejected = new("TierApplicationRejected", Verb: "rejected tier application for");

    public static readonly AuditActionDefinition TierDowngraded = new("TierDowngraded");

    // From the temporary /Governance/Applications/Admin/TermExpiry screen.
    public static readonly AuditActionDefinition TierTermExpiryCorrected = new("TierTermExpiryCorrected");

    public static readonly AuditActionDefinition AssemblyVoteOpened = new("AssemblyVoteOpened");

    public static readonly AuditActionDefinition AssemblyVoteStopped = new("AssemblyVoteStopped");

    public static readonly AuditActionDefinition AssemblyVoteExtended = new("AssemblyVoteExtended");

    public static readonly AuditActionDefinition AssemblyVoteCancelled = new("AssemblyVoteCancelled");

    public static readonly AuditActionDefinition AssemblyVoteClosed = new("AssemblyVoteClosed");

    public static readonly AuditActionDefinition AssemblyVotePeeked = new("AssemblyVotePeeked");

    public static readonly AuditActionDefinition AssemblyBallotCast = new("AssemblyBallotCast");

    public static readonly AuditActionDefinition AssemblyBallotChanged = new("AssemblyBallotChanged");

    public static readonly AuditActionDefinition AssemblyBallotsViewed = new("AssemblyBallotsViewed");

    public static readonly AuditActionDefinition AssemblyVoteRosterMerged = new("AssemblyVoteRosterMerged");

    public static readonly AuditActionDefinition AssemblyVoteRemindersSent = new("AssemblyVoteRemindersSent");
}
