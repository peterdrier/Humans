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
/// The set is the <c>GoogleAccounts_*</c> keys, which the section's own
/// <c>Views/Google/Accounts.cshtml</c> is the sole renderer of, plus the
/// <c>GoogleIntegration_Email_*</c> and <c>GoogleIntegration_Notification_*</c> keys used by
/// <c>Services/GoogleIntegrationEmails.cs</c> and <c>Services/EmailProvisioningService.cs</c>.
/// Everything else on the section's pages is admin-only English.
/// </remarks>
public class GoogleIntegrationResource;
