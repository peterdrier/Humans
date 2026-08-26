namespace Humans.Onboarding;

/// <summary>
/// Marker type for Onboarding's resource set. The <c>.resx</c> files sit beside this file
/// on purpose: the SDK derives the manifest name from the adjacent same-named <c>.cs</c>
/// file's namespace, not from the folder path, so this must stay
/// <c>namespace Humans.Onboarding</c> — <c>Humans.Onboarding.Resources</c> would make
/// every string in the set fall back to its raw key at runtime (design §3).
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers section resource markers via
/// <c>GetExportedTypes()</c>; an internal marker is skipped in silence (§15.3b).
/// The set is the <c>Onboarding_*</c>, <c>OnboardingReview_*</c> and
/// <c>OnboardingBanner_*</c> keys. Two callers outside the section render one apiece and
/// inject <c>IStringLocalizer&lt;OnboardingResource&gt;</c> rather than the prefixes being
/// split: Shifts' <c>ShiftsController</c> name-gate message
/// (<c>Onboarding_NameRequiredBeforeShifts</c>) and Governance's board-voting detail page,
/// which reuses four <c>OnboardingReview_*</c> labels for the applicant summary
/// (design §15 step 3b, Budget's "the key goes home" direction).
/// <para>
/// The sidebar's "Review" entry is <em>not</em> in this set and is not localized at all:
/// <c>AdminNavItem.Label</c> is a raw string rendered as-is by Shell's
/// <c>AdminSidebarViewComponent</c>, so <c>SectionAdminNav.cs</c> hard-codes English like
/// every other section's contribution.
/// </para>
/// </remarks>
public class OnboardingResource;
