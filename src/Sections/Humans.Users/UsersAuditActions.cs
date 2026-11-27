using Humans.AuditLog.Contracts;

namespace Humans.Users;

/// <summary>The audit actions Users writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class UsersAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition MemberSuspended = new("MemberSuspended", AuditKept.Forever, Verb: "suspended", QuickFilter: "Suspensions");

    public static readonly AuditActionDefinition MemberUnsuspended = new("MemberUnsuspended", AuditKept.Forever, Verb: "unsuspended");

    public static readonly AuditActionDefinition AccountAnonymized = new("AccountAnonymized", AuditKept.Forever);

    public static readonly AuditActionDefinition DeletionHeldForTicket = new("DeletionHeldForTicket", AuditKept.Forever);

    public static readonly AuditActionDefinition MembershipsRevokedOnDeletionRequest = new("MembershipsRevokedOnDeletionRequest", AuditKept.Forever);

    public static readonly AuditActionDefinition AccountMergeAccepted = new("AccountMergeAccepted", AuditKept.Forever);

    public static readonly AuditActionDefinition AccountMergeRejected = new("AccountMergeRejected", AuditKept.Forever);

    // A merge that faulted part-way. Entity is the archived account, related entity the survivor.
    public static readonly AuditActionDefinition AccountMergeFailed = new("AccountMergeFailed", AuditKept.Forever);

    public static readonly AuditActionDefinition ContactCreated = new("ContactCreated", AuditKept.Forever);

    public static readonly AuditActionDefinition GoogleEmailRenamed = new("GoogleEmailRenamed", AuditKept.Forever);

    public static readonly AuditActionDefinition AccountPurged = new("AccountPurged", AuditKept.Forever);

    public static readonly AuditActionDefinition UserEmailLinked = new("UserEmailLinked", AuditKept.Forever);

    public static readonly AuditActionDefinition UserEmailUnlinked = new("UserEmailUnlinked", AuditKept.Forever);

    public static readonly AuditActionDefinition UserEmailPrimarySet = new("UserEmailPrimarySet", AuditKept.Forever);

    public static readonly AuditActionDefinition UserEmailPrimaryCleared = new("UserEmailPrimaryCleared", AuditKept.Forever);

    public static readonly AuditActionDefinition UserEmailDeleted = new("UserEmailDeleted", AuditKept.Forever);

    public static readonly AuditActionDefinition UserEmailVisibilityChanged = new("UserEmailVisibilityChanged", AuditKept.Forever);

    public static readonly AuditActionDefinition UserEmailAdded = new("UserEmailAdded", AuditKept.Forever);

    public static readonly AuditActionDefinition UserEmailManuallyVerified = new("UserEmailManuallyVerified", AuditKept.Forever);

    public static readonly AuditActionDefinition OrphanUserEmailDeleted = new("OrphanUserEmailDeleted", AuditKept.Forever);

    public static readonly AuditActionDefinition LegacyIdentityEmailBackfilled = new("LegacyIdentityEmailBackfilled", AuditKept.Forever);

    public static readonly AuditActionDefinition OAuthRenameCollision = new("OAuthRenameCollision", AuditKept.Forever);

    public static readonly AuditActionDefinition OAuthRenameCollisionBlocked = new("OAuthRenameCollisionBlocked", AuditKept.Forever);

    public static readonly AuditActionDefinition UserEmailDisplacedByOAuthRename = new("UserEmailDisplacedByOAuthRename", AuditKept.Forever);

    // Once per pair, the first time the scan sees it. Entity is the lower id, related entity the higher.
    public static readonly AuditActionDefinition DuplicateAccountFlagged = new("DuplicateAccountFlagged", AuditKept.Forever);

    public static readonly AuditActionDefinition StubProfileBackfilled = new("StubProfileBackfilled", AuditKept.Forever);

    public static readonly AuditActionDefinition AccountMarkedFake = new("AccountMarkedFake", AuditKept.Forever);

    // Entity "User" + the subject's id is what the member's own medical page lists.
    public static readonly AuditActionDefinition MedicalConditionsRevealed = new("MedicalConditionsRevealed", AuditKept.Forever);
}
