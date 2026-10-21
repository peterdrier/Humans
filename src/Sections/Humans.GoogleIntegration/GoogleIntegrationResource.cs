namespace Humans.GoogleIntegration;

/// <summary>
/// Marker type for GoogleIntegration's resource set. The <c>.resx</c> files sit beside this
/// file on purpose: the SDK derives the manifest name from the adjacent same-named <c>.cs</c>
/// file's namespace, not from the folder path, so this must stay
/// <c>namespace Humans.GoogleIntegration</c> — <c>Humans.GoogleIntegration.Resources</c> would
/// make every string fall back to its raw key at runtime.
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers section resource markers via
/// <c>GetExportedTypes()</c>; an internal marker is skipped in silence (§15.3b).
/// Keys use the <c>GoogleIntegration_</c> section prefix. The account page renders
/// <c>GoogleIntegration_Accounts_*</c>; email templates and member resource views
/// use the same resource set. Other operator pages use English copy.
/// </remarks>
public class GoogleIntegrationResource;
