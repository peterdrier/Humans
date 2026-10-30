using Humans.AuditLog.Contracts;

namespace Humans.Users;

/// <summary>The audit actions Users writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class UsersAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition MemberSuspended = new("MemberSuspended", Verb: "suspended", QuickFilter: "Suspensions");

    public static readonly AuditActionDefinition MemberUnsuspended = new("MemberUnsuspended", Verb: "unsuspended");

    public static readonly AuditActionDefinition AccountAnonymized = new("AccountAnonymized");

    public static readonly AuditActionDefinition MembershipsRevokedOnDeletionRequest = new("MembershipsRevokedOnDeletionRequest");

    public static readonly AuditActionDefinition AccountMergeAccepted = new("AccountMergeAccepted");

    public static readonly AuditActionDefinition AccountMergeRejected = new("AccountMergeRejected");

    public static readonly AuditActionDefinition ContactCreated = new("ContactCreated");

    public static readonly AuditActionDefinition GoogleEmailRenamed = new("GoogleEmailRenamed");

    public static readonly AuditActionDefinition AccountPurged = new("AccountPurged");

    public static readonly AuditActionDefinition UserEmailLinked = new("UserEmailLinked");

    public static readonly AuditActionDefinition UserEmailUnlinked = new("UserEmailUnlinked");

    public static readonly AuditActionDefinition UserEmailPrimarySet = new("UserEmailPrimarySet");

    public static readonly AuditActionDefinition UserEmailPrimaryCleared = new("UserEmailPrimaryCleared");

    public static readonly AuditActionDefinition UserEmailDeleted = new("UserEmailDeleted");

    public static readonly AuditActionDefinition UserEmailVisibilityChanged = new("UserEmailVisibilityChanged");

    public static readonly AuditActionDefinition UserEmailAdded = new("UserEmailAdded");

    public static readonly AuditActionDefinition UserEmailManuallyVerified = new("UserEmailManuallyVerified");

    public static readonly AuditActionDefinition OrphanUserEmailDeleted = new("OrphanUserEmailDeleted");

    public static readonly AuditActionDefinition LegacyIdentityEmailBackfilled = new("LegacyIdentityEmailBackfilled");

    public static readonly AuditActionDefinition OAuthRenameCollision = new("OAuthRenameCollision");

    public static readonly AuditActionDefinition OAuthRenameCollisionBlocked = new("OAuthRenameCollisionBlocked");

    public static readonly AuditActionDefinition UserEmailDisplacedByOAuthRename = new("UserEmailDisplacedByOAuthRename");

    // Once per pair, the first time the scan sees it. Entity is the lower id, related entity the higher.
    public static readonly AuditActionDefinition DuplicateAccountFlagged = new("DuplicateAccountFlagged");

    public static readonly AuditActionDefinition StubProfileBackfilled = new("StubProfileBackfilled");
}
