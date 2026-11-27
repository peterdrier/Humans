using Humans.Audit.Contracts;

namespace Humans.Governance;

/// <summary>The audit actions Governance writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class GovernanceAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition TierApplicationApproved = new("TierApplicationApproved", AuditKept.Forever, Verb: "approved tier application for");

    public static readonly AuditActionDefinition TierApplicationRejected = new("TierApplicationRejected", AuditKept.Forever, Verb: "rejected tier application for");

    public static readonly AuditActionDefinition TierDowngraded = new("TierDowngraded", AuditKept.Forever);

    // From the temporary /Governance/Applications/Admin/TermExpiry screen.
    public static readonly AuditActionDefinition TierTermExpiryCorrected = new("TierTermExpiryCorrected", AuditKept.Forever);

    public static readonly AuditActionDefinition GovernanceBodyLinksCopied = new("GovernanceBodyLinksCopied", AuditKept.Forever);

    public static readonly AuditActionDefinition GovernanceBodyLinksChanged = new("GovernanceBodyLinksChanged", AuditKept.Forever);

    public static readonly AuditActionDefinition BoardPositionSet = new("BoardPositionSet", AuditKept.Forever);

    public static readonly AuditActionDefinition AssemblyVoteOpened = new("AssemblyVoteOpened", AuditKept.Forever);

    public static readonly AuditActionDefinition AssemblyVoteStopped = new("AssemblyVoteStopped", AuditKept.Forever);

    public static readonly AuditActionDefinition AssemblyVoteExtended = new("AssemblyVoteExtended", AuditKept.Forever);

    public static readonly AuditActionDefinition AssemblyVoteCancelled = new("AssemblyVoteCancelled", AuditKept.Forever);

    public static readonly AuditActionDefinition AssemblyVoteClosed = new("AssemblyVoteClosed", AuditKept.Forever);

    public static readonly AuditActionDefinition AssemblyVotePeeked = new("AssemblyVotePeeked", AuditKept.Forever);

    public static readonly AuditActionDefinition AssemblyBallotCast = new("AssemblyBallotCast", AuditKept.Forever);

    public static readonly AuditActionDefinition AssemblyBallotChanged = new("AssemblyBallotChanged", AuditKept.Forever);

    public static readonly AuditActionDefinition AssemblyBallotsViewed = new("AssemblyBallotsViewed", AuditKept.Forever);

    public static readonly AuditActionDefinition AssemblyVoteRosterMerged = new("AssemblyVoteRosterMerged", AuditKept.Forever);

    public static readonly AuditActionDefinition AssemblyVoteRemindersSent = new("AssemblyVoteRemindersSent", AuditKept.Forever);
}
