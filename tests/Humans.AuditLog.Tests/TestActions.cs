using Humans.AuditLog.Contracts;

namespace Humans.AuditLog.Tests;

/// <summary>The hub's tests declare their own actions; the sections' classes are internal to them.</summary>
internal sealed class TestActions : AuditActionContributor
{
    public static readonly AuditActionDefinition ShiftSignupCreated =
        new("ShiftSignupCreated", AuditKept.Forever, Verb: "created signup for", SelfVerb: "signed up for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition ShiftSignupVoluntold =
        new("ShiftSignupVoluntold", AuditKept.Forever, Verb: "voluntold", DescriptionIsTail: true);

    public static readonly AuditActionDefinition ShiftSignupBailed =
        new("ShiftSignupBailed", AuditKept.Forever, Verb: "bailed", SelfVerb: "bailed from", DescriptionIsTail: true);

    public static readonly AuditActionDefinition TeamJoinedDirectly = new("TeamJoinedDirectly", AuditKept.Forever, Verb: "joined");

    public static readonly AuditActionDefinition TeamMemberAdded = new("TeamMemberAdded", AuditKept.Forever, Verb: "added");

    public static readonly AuditActionDefinition VolunteerApproved = new("VolunteerApproved", AuditKept.Forever, Verb: "approved");

    public static readonly AuditActionDefinition RoleEnded = new("RoleEnded", AuditKept.Forever, Verb: "ended role of");
    public static readonly AuditActionDefinition WorkgroupDispositionRecorded = new("WorkgroupDispositionRecorded", AuditKept.Forever);
    public static readonly AuditActionDefinition GoogleResourceAccessGranted = new("GoogleResourceAccessGranted", AuditKept.Forever);
    public static readonly AuditActionDefinition RoleAssigned =
        new("RoleAssigned", AuditKept.Forever, Verb: "assigned role to", DescriptionIsTail: true, QuickFilter: "Roles");

    public static readonly AuditActionDefinition MemberSuspended =
        new("MemberSuspended", AuditKept.Forever, Verb: "suspended", QuickFilter: "Suspensions");

    public static readonly AuditActionDefinition AnomalousPermissionDetected =
        new("AnomalousPermissionDetected", AuditKept.Forever, Alert: true, QuickFilter: "Anomalous Permissions");

    public static readonly AuditActionDefinition CampaignCreated = new("CampaignCreated", AuditKept.Forever);

    public static readonly AuditActionDefinition BudgetLineChanged = new("BudgetLineChanged", AuditKept.RetentionPeriod);

    public static readonly AuditActionDefinition StoreProductPriceChanged = new("StoreProductPriceChanged", AuditKept.Forever);
}
