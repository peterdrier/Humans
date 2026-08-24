using System.Reflection;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Microsoft.Extensions.Localization;

namespace Humans.Onboarding.Tests.Architecture;

/// <summary>
/// Architecture tests for the Onboarding section.
///
/// Five absence assertions from nobodies-collective/Humans#1465 (no repository, no DbContext,
/// interface-only dependencies, exports exactly N types) were deleted again — they existed
/// only because Onboarding.md claimed them. `memory/architecture/no-tests-for-absences.md`.
/// </summary>
public class OnboardingArchitectureTests
{
    private static Assembly SectionAssembly => typeof(OnboardingResource).Assembly;

    /// <summary>
    /// Every localizer the section injects names one of the three sets it is allowed to render
    /// from. A key looked up against the wrong set renders as its own name with a green build —
    /// <c>OnboardingLocalizerBindingTests</c> is the per-key half of the same guard.
    /// </summary>
    [HumansFact]
    public void EveryLocalizerNamesOneOfTheSectionsThreeResourceSets()
    {
        string[] allowed = ["OnboardingResource", "SharedResource", "ConsentResource"];

        var offenders = SectionTypes()
            .SelectMany(t => t.GetConstructors().Select(c => (Type: t, Ctor: c)))
            .SelectMany(x => x.Ctor.GetParameters().Select(p => (x.Type, Param: p)))
            .Where(x => x.Param.ParameterType.IsGenericType
                     && (x.Param.ParameterType.GetGenericTypeDefinition() == typeof(IStringLocalizer<>)
                      || x.Param.ParameterType.GetGenericTypeDefinition().Name.StartsWith("IHtmlLocalizer", StringComparison.Ordinal)))
            .Select(x => (x.Type, Marker: x.Param.ParameterType.GetGenericArguments()[0].Name))
            .Where(x => !allowed.Contains(x.Marker, StringComparer.Ordinal))
            .Select(x => $"{x.Type.Name} -> {x.Marker}")
            .ToList();

        offenders.Should().BeEmpty(
            because: "the section renders its own copy, the shared copy and Consent's copy — nothing else");
    }

    /// <summary>
    /// Types the section declares in its own source. Razor compiles each view into a public
    /// generated class, and the compiler emits its own helpers; neither is section surface.
    /// </summary>
    private static IEnumerable<Type> SectionTypes() =>
        SectionAssembly.GetTypes().Where(IsDeclaredInSource);

    private static bool IsDeclaredInSource(Type type) =>
        !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
        && type.Namespace?.StartsWith("AspNetCoreGeneratedDocument", StringComparison.Ordinal) != true
        && type.Namespace?.StartsWith("Humans.Onboarding", StringComparison.Ordinal) == true;
}
