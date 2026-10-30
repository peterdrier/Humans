using Humans.AuditLog.Contracts;

namespace Humans.AuditLog.Tests;

/// <summary>The hub's tests declare their own actions; the sections' classes are internal to them.</summary>
internal sealed class TestActions : AuditActionContributor
{
    public static readonly AuditActionDefinition ShiftSignupCreated =
        new("ShiftSignupCreated", Verb: "created signup for", SelfVerb: "signed up for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition ShiftSignupVoluntold =
        new("ShiftSignupVoluntold", Verb: "voluntold", DescriptionIsTail: true);

    public static readonly AuditActionDefinition ShiftSignupBailed =
        new("ShiftSignupBailed", Verb: "bailed", SelfVerb: "bailed from", DescriptionIsTail: true);

    public static readonly AuditActionDefinition TeamJoinedDirectly = new("TeamJoinedDirectly", Verb: "joined");

    public static readonly AuditActionDefinition TeamMemberAdded = new("TeamMemberAdded", Verb: "added");

    public static readonly AuditActionDefinition VolunteerApproved = new("VolunteerApproved", Verb: "approved");

    public static readonly AuditActionDefinition RoleAssigned =
        new("RoleAssigned", Verb: "assigned role to", DescriptionIsTail: true, QuickFilter: "Roles");

    public static readonly AuditActionDefinition MemberSuspended =
        new("MemberSuspended", Verb: "suspended", QuickFilter: "Suspensions");

    public static readonly AuditActionDefinition AnomalousPermissionDetected =
        new("AnomalousPermissionDetected", Alert: true, QuickFilter: "Anomalous Permissions");

    public static readonly AuditActionDefinition CampaignCreated = new("CampaignCreated");

    public static readonly AuditActionDefinition StoreProductPriceChanged = new("StoreProductPriceChanged");
}
