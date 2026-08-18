namespace Humans.GoogleIntegration;

/// <summary>
/// Marker type for GoogleIntegration's resource set. The <c>.resx</c> files sit beside this
/// file on purpose: the SDK derives the manifest name from the adjacent same-named <c>.cs</c>
/// file's namespace, not from the folder path, so this must stay
/// <c>namespace Humans.GoogleIntegration</c> — <c>Humans.GoogleIntegration.Resources</c> would
/// make every string fall back to its raw key at runtime (design §3).
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers section resource markers via
/// <c>GetExportedTypes()</c>; an internal marker is skipped in silence (§15.3b).
/// The set is the three <c>GoogleAccounts_*</c> keys, which the section's own
/// <c>Views/Google/Accounts.cshtml</c> is the sole renderer of. The nine dead
/// <c>GoogleSync_*</c> keys stay in <c>SharedResource</c>: nothing renders them at all, so
/// there is no renderer to carve by, and importing dead copy into a new set is noise.
/// Everything else on the section's pages is admin-only English.
/// </remarks>
public class GoogleIntegrationResource;
