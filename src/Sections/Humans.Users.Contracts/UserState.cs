namespace Humans.Users.Contracts;

/// <summary>
/// Stored by name and written at each transition; nothing derives it on read. Only <see cref="Active"/> reaches the
/// full app. Precedence (most final wins): <c>Merged &gt; Deleted &gt; Rejected &gt; AdminSuspended/Suspended &gt;
/// DeletePending &gt; Bare &gt; Active</c>.
/// </summary>
public enum UserState
{
    /// <summary>Required names not all filled. Routed to name entry.</summary>
    Bare = 0,

    /// <summary>The only state with full app access.</summary>
    Active = 1,

    /// <summary>Within the deletion grace window. Reaches only the cancel-deletion screen.</summary>
    DeletePending = 2,

    /// <summary>Missing required consents. The account-status wall, with a way to complete them.</summary>
    Suspended = 3,

    /// <summary>The account-status wall, with the rejection reason when present.</summary>
    Rejected = 4,

    /// <summary>Purged or anonymized. The account-status wall.</summary>
    Deleted = 5,

    /// <summary>A merge tombstone. Can't sign in; the wall is a defensive catch-all.</summary>
    Merged = 6,

    /// <summary>The account-status wall until an admin unsuspends.</summary>
    AdminSuspended = 7,
}
