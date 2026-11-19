namespace Humans.Governance;

/// <summary>
/// The .resx files sit beside this file, so it must stay in <c>namespace Humans.Governance</c>: the manifest name comes
/// from this file's namespace, and any other makes every string fall back to its key. Public because the boot
/// localization check finds markers through <c>GetExportedTypes()</c>.
/// </summary>
/// <remarks>
/// Tier-application copy uses <c>Governance_Application_*</c> here. <c>ApplicationStatus_*</c> and the
/// <c>Profile*</c>/<c>Common_*</c> labels stay shared with Users and Consent.
/// </remarks>
public class GovernanceResource;
