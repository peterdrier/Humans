namespace Humans.Onboarding;

/// <summary>
/// Stays in <c>namespace Humans.Onboarding</c> beside the .resx: the SDK derives the manifest name from it,
/// and any other namespace renders every string as its key. Public because the boot localization
/// diagnostic finds markers via <c>GetExportedTypes()</c>.
/// </summary>
public class OnboardingResource;
