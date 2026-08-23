using System.Reflection;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Humans.Onboarding.Contracts;
using Humans.Onboarding.Services;
using Microsoft.Extensions.Localization;

namespace Humans.Onboarding.Tests.Architecture;

/// <summary>
/// Architecture tests for the Onboarding section — migrated to the §15
/// pattern in issue #553. Onboarding is a pure orchestrator (owns no tables):
/// no repository — the constructor must only take cross-section service interfaces.
/// </summary>
public class OnboardingArchitectureTests
{
    private static Assembly SectionAssembly => typeof(OnboardingResource).Assembly;
    private static Assembly LeafAssembly => typeof(IOnboardingIntake).Assembly;

    [HumansFact]
    public void OnboardingService_DependsOnlyOnServiceInterfaces()
    {
        var ctor = typeof(OnboardingService).GetConstructors().Single();
        var forbidden = ctor.GetParameters()
            .Where(p => p.ParameterType != typeof(NodaTime.IClock))
            .Where(p =>
                // Services are interfaces under Humans.Base.Interfaces.*
                // (IUserService, IApplicationDecisionService, IAuditLogService, ...)
                // plus well-known cross-cuts (ILogger, IMetrics, ...).
                !p.ParameterType.IsInterface)
            .ToList();

        forbidden.Should().BeEmpty(
            because: "every OnboardingService dependency must be an interface to preserve its orchestrator shape");
    }

    [HumansFact]
    public void OnboardingService_TakesNoRepository()
    {
        var repositories = typeof(OnboardingService).GetConstructors().Single()
            .GetParameters()
            .Where(p => p.ParameterType.Name.EndsWith("Repository", StringComparison.Ordinal))
            .Select(p => p.ParameterType.Name)
            .ToList();

        repositories.Should().BeEmpty(
            because: "Onboarding owns no tables — every write goes through the owning section's service");
    }

    /// <summary>
    /// The assembly-level "no EF" assertion restated on the constructors: a section assembly may
    /// legitimately reference EF (most do, through a leaf), an orchestrator may not touch it.
    /// </summary>
    [HumansFact]
    public void NoTypeInTheSection_TakesADbContextOrStore()
    {
        var offenders = SectionTypes()
            .SelectMany(t => t.GetConstructors().Select(c => (Type: t, Ctor: c)))
            .SelectMany(x => x.Ctor.GetParameters().Select(p => (x.Type, Param: p)))
            .Where(x => IsDataAccess(x.Param.ParameterType))
            .Select(x => $"{x.Type.Name}({x.Param.ParameterType.Name} {x.Param.Name})")
            .ToList();

        offenders.Should().BeEmpty(
            because: "the section owns no tables, so nothing in it may reach a DbContext, "
            + "an IDbContextFactory<> or a store");
    }

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

    [HumansFact]
    public void TheSectionExportsOnlySectionAndItsResourceMarker()
    {
        PublicTypeNames(SectionAssembly).Should().BeEquivalentTo(
            ["Section", "OnboardingResource"],
            because: "controllers, view components, services and view models are all section-internal; "
            + "Shell discovers them through its feature providers, not through the public surface");
    }

    [HumansFact]
    public void TheLeafExportsOnlyTheFourCarvedTypes()
    {
        PublicTypeNames(LeafAssembly).Should().BeEquivalentTo(
            ["IOnboardingIntake", "IOnboardingWidgetState", "OnboardingWidgetStep", "OnboardingResult"],
            because: "the leaf is Onboarding's whole cross-section surface — everything with no "
            + "consumer outside the section stays internal");
    }

    /// <summary>
    /// Types the section declares in its own source. Razor compiles each view into a public
    /// generated class, and the compiler emits its own helpers; neither is section surface.
    /// </summary>
    private static IEnumerable<Type> SectionTypes() =>
        SectionAssembly.GetTypes().Where(IsDeclaredInSource);

    private static IEnumerable<string> PublicTypeNames(Assembly assembly) =>
        assembly.GetTypes()
            .Where(t => t.IsPublic && IsDeclaredInSource(t))
            .Select(t => t.Name);

    private static bool IsDeclaredInSource(Type type) =>
        !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
        && type.Namespace?.StartsWith("AspNetCoreGeneratedDocument", StringComparison.Ordinal) != true
        && type.Namespace?.StartsWith("Humans.Onboarding", StringComparison.Ordinal) == true;

    private static bool IsDataAccess(Type type)
    {
        var name = type.Name;
        return name.EndsWith("DbContext", StringComparison.Ordinal)
            || name.StartsWith("IDbContextFactory", StringComparison.Ordinal)
            || name.EndsWith("Store", StringComparison.Ordinal)
            || name.EndsWith("Store`1", StringComparison.Ordinal);
    }
}
