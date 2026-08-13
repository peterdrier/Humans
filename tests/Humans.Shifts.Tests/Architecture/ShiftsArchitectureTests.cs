using System.Reflection;
using AwesomeAssertions;
using Humans.Onboarding;
using Humans.UI;
using Microsoft.Extensions.Localization;
using Xunit;

namespace Humans.Shifts.Tests.Architecture;

/// <summary>
/// The section-boundary assertions for the Shifts move into
/// <c>src/Sections/Humans.Shifts</c> (nobodies-collective/Humans#866, G5), as distinct from
/// the three service-scoped architecture suites beside this file.
/// </summary>
public class ShiftsArchitectureTests
{
    [HumansFact]
    public void SectionTypesLocalizeThroughTheSectionsOwnResourceSet()
    {
        // A view is safe by construction — Views/_ViewImports.cshtml rebinds Localizer in one
        // line — but a *controller* left on IStringLocalizer<SharedResource> keeps compiling
        // and renders its carved keys as raw key names on exactly the POST and failure paths a
        // render test does not reach (§15 step 3b, Surveys' finding). Governance's two-marker
        // form, with three markers because two sets are deliberately still read:
        //
        //   ShiftsResource     — the section's own 328 keys.
        //   SharedResource     — ShiftProfileController's Profile_Updated toast, which is
        //                        Shell's profile vocabulary, plus the six Shifts_ keys pinned
        //                        to Humans.UI's set by a renderer this assembly cannot be
        //                        referenced from (see ShiftsResource's remarks).
        //   OnboardingResource — ShiftsController renders Onboarding's name-gate copy before
        //                        it will let a nameless volunteer browse; a resource marker is
        //                        a section type, which is why Humans.Shifts references
        //                        Humans.Onboarding rather than its leaf.
        //
        // Sweeps method parameters as well as constructor ones (Debug's rule): an action that
        // takes the localizer as an argument is the same failure.
        var allowed = new[] { typeof(ShiftsResource), typeof(SharedResource), typeof(OnboardingResource) };

        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                                 | BindingFlags.Instance | BindingFlags.Static;

        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors(All).SelectMany(c => c.GetParameters())
                .Concat(t.GetMethods(All).SelectMany(m => m.GetParameters()))
                .Select(p => (Type: t, p.ParameterType)))
            .Where(x => x.ParameterType.IsGenericType
                        && x.ParameterType.GetGenericTypeDefinition() == typeof(IStringLocalizer<>))
            .Where(x => !allowed.Contains(x.ParameterType.GetGenericArguments()[0]))
            .Select(x => $"{x.Type.FullName}:{x.ParameterType.GetGenericArguments()[0].Name}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "binding any other resource set renders the raw key in all six languages");
    }

    [HumansFact]
    public void TheResourceMarkerIsPublicSoBootDiscoveryFindsIt()
    {
        // SectionDiscoveryExtensions.SectionResourceTypes() reads GetExportedTypes() and skips
        // an internal marker in silence, so the boot localization diagnostic would report a
        // healthy app while 328 keys resolved to their own names (§15 step 3b).
        typeof(ShiftsResource).IsPublic.Should().BeTrue();

        // The SDK derives the manifest name from this file's namespace, not its folder, so
        // Humans.Shifts.Resources here would degrade the whole set to raw keys (design §3).
        typeof(ShiftsResource).Namespace.Should().Be("Humans.Shifts");
    }
}
