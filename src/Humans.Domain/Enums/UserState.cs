namespace Humans.Domain.Enums;

/// <summary>
/// Lifecycle state of a <see cref="Entities.User"/> — the single source of truth for access.
/// Stored as a nullable string column on <c>users</c> (<see cref="Entities.User.State"/>) via
/// <c>HasConversion&lt;string&gt;()</c>. Written at each transition point; legacy rows holding
/// <c>null</c> are classified once on first read and persisted (see
/// <c>UserService.GetUserInfoAsync</c> + <c>IUserRepository.WriteBackUserStateIfNullAsync</c>),
/// mirroring the seed mechanism the superseded <see cref="ProfileState"/> used.
///
/// <para><b>Access rule:</b> the full app is reachable only when state is
/// <see cref="Active"/>. <see cref="DeletePending"/> reaches only the cancel-deletion screen.
/// <see cref="Bare"/> is routed to name entry. <see cref="Suspended"/>/<see cref="AdminSuspended"/>/
/// <see cref="Rejected"/>/<see cref="Deleted"/>/<see cref="Merged"/> are shown the account-status wall only.</para>
///
/// <para><b>Precedence</b> (most-final wins, used by the classify helper and the seed when
/// more than one underlying signal is present):
/// <c>Merged &gt; Deleted &gt; Rejected &gt; AdminSuspended/Suspended &gt; DeletePending &gt; Bare &gt; Active</c>.</para>
/// </summary>
public enum UserState
{
    /// <summary>Account exists but required name fields (BurnerName + FirstName + LastName)
    /// are not all filled. Routed to name entry; no app access yet.</summary>
    Bare = 0,

    /// <summary>Required name fields filled and none of the excluding states apply.
    /// The only state with full app access.</summary>
    Active = 1,

    /// <summary>User requested account deletion and is within the cancellation grace window.
    /// Reaches only the cancel-deletion screen.</summary>
    DeletePending = 2,

    /// <summary>Suspended for missing required consents. Shown the account-status wall with a consent-completion path.</summary>
    Suspended = 3,

    /// <summary>Signup rejected (<see cref="Entities.Profile.RejectedAt"/> set). Shown the
    /// account-status wall, including the rejection reason when present.</summary>
    Rejected = 4,

    /// <summary>Account deletion completed (purged/anonymized). Shown the account-status wall.</summary>
    Deleted = 5,

    /// <summary>Account folded into another via merge (tombstone). Cannot sign in; the wall is a
    /// defensive catch-all.</summary>
    Merged = 6,

    /// <summary>Administratively suspended. Shown the account-status wall until an admin unsuspends.</summary>
    AdminSuspended = 7,
}
