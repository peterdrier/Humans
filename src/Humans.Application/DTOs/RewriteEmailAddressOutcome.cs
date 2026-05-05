namespace Humans.Application.DTOs;

/// <summary>
/// Outcome of <c>RewriteEmailAddressAsync</c>. The caller (typically the OAuth
/// rename detector) needs to distinguish "no row to rewrite" (silent no-op),
/// "rewrote successfully", "the new address already belongs to this same user
/// so the source row was dropped instead of UPDATEd" (alternate-address
/// reorder), and "the new address is already on a different user" (cross-user
/// collision — surface to admins via the duplicate-account flow rather than
/// throwing on a unique-index violation).
/// </summary>
public enum RewriteEmailAddressOutcome
{
    /// <summary>No row matched <c>oldEmail</c> for this user; nothing changed.</summary>
    SourceRowNotFound = 0,

    /// <summary>The source row's address was rewritten to <c>newEmail</c>.</summary>
    Rewritten = 1,

    /// <summary>
    /// A row with <c>newEmail</c> already exists for the same user. The source
    /// row (with <c>oldEmail</c>) was dropped; the existing target row was
    /// marked verified. Single transaction.
    /// </summary>
    MergedIntoExistingRowForSameUser = 2,

    /// <summary>
    /// A row with <c>newEmail</c> already exists for a DIFFERENT user. No
    /// UPDATE was attempted. Caller should log a warning and let the existing
    /// duplicate-account detection flow surface the conflict to admins.
    /// </summary>
    CrossUserConflict = 3,
}
