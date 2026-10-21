namespace Humans.Gdpr;

/// <summary>
/// Marker type for the Gdpr resource set. The <c>.resx</c> files sit beside this file on
/// purpose: the SDK derives the manifest name from the adjacent same-named <c>.cs</c> file's
/// namespace, so this must stay <c>namespace Humans.Gdpr</c>.
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers section resource markers via
/// <c>GetExportedTypes()</c>; an internal marker is skipped in silence. The set holds the copy
/// of the account-status wall and the pending-deletion landing (<c>AccountStatus_*</c>,
/// <c>AccountDeletion_*</c>) and the flash messages of the deletion request/cancel actions
/// (<c>Profile_Deletion*</c>, <c>Profile_NoDeletionPending</c>). Users keeps its own copy of
/// the keys it still reads: the guest-dashboard deletion flashes and the suspension
/// notification text (<c>AccountStatus_HeadingSuspended</c> and friends).
/// </remarks>
public class GdprResource;
