namespace Humans.Consent;

/// <summary>
/// Marker type for Consent's resource set. The <c>.resx</c> files sit beside this file on
/// purpose: the SDK derives the manifest name from the adjacent same-named <c>.cs</c>
/// file's namespace, not from the folder path, so this must stay
/// <c>namespace Humans.Consent</c> — <c>Humans.Consent.Resources</c> would make every
/// string in the set fall back to its raw key at runtime (design §3).
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers section resource markers via
/// <c>GetExportedTypes()</c>; an internal marker is skipped in silence (§15.3b).
/// The set is the 36 <c>Consent_*</c>, <c>ConsentReview_*</c> and <c>ConsentIndex_*</c>
/// keys. Three of them are rendered from outside the section — Shell's onboarding widget
/// signs the same documents through the same partial, and Governance's statutes page reuses
/// one empty-state string — so those callers inject
/// <c>IStringLocalizer&lt;ConsentResource&gt;</c> directly rather than the set being split
/// (design §15 step 3b, Budget's "the key goes home" direction). <c>Nav_Consents</c> stays
/// in <c>SharedResource</c>: its renderer is <c>Humans.UI</c>'s login partial, which is
/// Base chrome.
/// </remarks>
public class ConsentResource { }
