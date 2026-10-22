namespace Humans.Governance;

/// <summary>
/// The .resx files sit beside this file, so it must stay in <c>namespace Humans.Governance</c>: the manifest name comes
/// from this file's namespace, and any other makes every string fall back to its key. Public because the boot
/// localization check finds markers through <c>GetExportedTypes()</c>.
/// </summary>
/// <remarks>
/// <c>Application_*</c>, <c>ApplicationStatus_*</c> and the <c>Profile*</c>/<c>OnboardingReview_*</c>/<c>Common_*</c>
/// singles stay in <c>SharedResource</c>: profile setup and the Onboarding review queue render them too.
/// </remarks>
public class GovernanceResource;
