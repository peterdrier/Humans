namespace Humans.Governance;

/// <summary>
/// Marker type for Governance's resource set. The <c>.resx</c> files sit beside this file on
/// purpose: the SDK derives the manifest name from the adjacent same-named <c>.cs</c> file's
/// namespace, not from the folder path, so this must stay <c>namespace Humans.Governance</c> —
/// <c>Humans.Governance.Resources</c> would make every governance string fall back to its raw
/// key at runtime (design §3).
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers section resource markers via
/// <c>GetExportedTypes()</c>; an internal marker is skipped in silence (§15.3b).
/// Prefixes the section reads but does not own — <c>Application_*</c>,
/// <c>ApplicationStatus_*</c> and the <c>Profile*</c>/<c>OnboardingReview_*</c>/<c>Common_*</c>
/// singles — stay in <c>SharedResource</c>, because <c>ProfileController</c> renders the same
/// tier-application form during profile setup and the Onboarding review queue renders the
/// rest. Those call sites bind <c>SharedLocalizer</c>.
/// </remarks>
public class GovernanceResource;
