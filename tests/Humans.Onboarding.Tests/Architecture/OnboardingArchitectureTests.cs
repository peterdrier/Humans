using System.Reflection;
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
    [HumansFact]
    public void OnboardingService_HasNoRepositoryDependency()
    {
        var ctor = typeof(OnboardingService).GetConstructors().Single();
        var repositoryParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Repositories", StringComparison.Ordinal));

        repositoryParam.Should().BeNull(
            because: "Onboarding owns no tables — it must not inject repository interfaces, only section service interfaces (design-rules §9)");
    }

    [HumansFact]
    public void OnboardingService_DependsOnlyOnServiceInterfaces()
    {
        var ctor = typeof(OnboardingService).GetConstructors().Single();
        var forbidden = ctor.GetParameters()
            .Where(p => p.ParameterType != typeof(NodaTime.IClock))
            .Where(p =>
                // Services are interfaces under Humans.Application.Interfaces.*
                // (IUserService, IApplicationDecisionService, IAuditLogService, ...)
                // plus well-known cross-cuts (ILogger, IMetrics, ...).
                !p.ParameterType.IsInterface)
            .ToList();

        forbidden.Should().BeEmpty(
            because: "every OnboardingService dependency must be an interface to preserve its orchestrator shape");
    }

    /// <summary>
    /// The former assembly-level assertion — "<c>typeof(OnboardingService).Assembly</c> does
    /// not reference EF Core" — was a true statement about <c>Humans.Application</c> and is
    /// meaningless here: the section assembly could legitimately hold a repository. Restated
    /// on the constructors, which is what it was reaching for and is stronger (design §15
    /// step 11, Calendar's rule). Onboarding is an orchestrator, so the bar is higher than for
    /// a table-owning section: no data-access type of any kind.
    /// </summary>
    [HumansFact]
    public void SectionServices_TakeNoDataAccessDependency()
    {
        var offenders = SectionTypes()
            .SelectMany(t => t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            .SelectMany(c => c.GetParameters())
            .Where(p =>
            {
                var name = p.ParameterType.Name;
                var ns = p.ParameterType.Namespace ?? string.Empty;
                return name.EndsWith("DbContext", StringComparison.Ordinal)
                    || name.StartsWith("IDbContextFactory", StringComparison.Ordinal)
                    || ns.StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal)
                    || ns.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal);
            })
            .Select(p => $"{p.Member.DeclaringType!.Name}.{p.Name}")
            .ToList();

        offenders.Should().BeEmpty(
            because: "Onboarding owns no tables — no type in the section may take a DbContext, a context factory or a store");
    }

    /// <summary>
    /// Surveys' structural localizer guard in Governance's multi-marker form. Onboarding owns
    /// its 67 keys; <c>SharedResource</c> keeps the shared vocabulary the widget renders and
    /// the three <c>Onboarding_*Label</c> keys MVC's global data-annotation localizer resolves;
    /// and the widget's Consents step renders Consent's own copy. A type bound to any
    /// <i>fourth</i> set is the failure this catches — the one a render test cannot, because
    /// controller-resolved copy sits on POST and failure branches.
    /// </summary>
    [HumansFact]
    public void SectionTypes_LocalizeThroughAnAllowedResourceSet()
    {
        var allowed = new[] { "OnboardingResource", "SharedResource", "ConsentResource" };

        var offenders = SectionTypes()
            .SelectMany(t => t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters())
                .Concat(t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .SelectMany(m => m.GetParameters())))
            .Where(p => p.ParameterType.IsGenericType
                && p.ParameterType.GetGenericTypeDefinition() == typeof(IStringLocalizer<>))
            .Select(p => p.ParameterType.GetGenericArguments()[0].Name)
            .Where(n => !allowed.Contains(n, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "a section type bound to some other resource set renders its keys raw, in every language, with a green build");
    }

    /// <summary>
    /// HUM0034 is the build gate; this states the intent in the section's own terms so a new
    /// public type has to justify itself as a contract rather than slip in as surface.
    /// </summary>
    [HumansFact]
    public void OnlySectionMarkersArePublic()
    {
        var exported = typeof(OnboardingResource).Assembly
            .GetExportedTypes()
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        exported.Should().BeEquivalentTo(
            ["OnboardingResource", "Section"],
            because: "the cross-section surface lives in Humans.Onboarding.Contracts; the section itself exports only its DI entry point and its resource marker");
    }

    /// <summary>
    /// The leaf is deliberately narrow — two intake writes, the widget-step read and the
    /// result record. Anything else that leaves the section should be a deliberate decision,
    /// not a drift.
    /// </summary>
    [HumansFact]
    public void ContractsLeafCarriesOnlyTheCarvedSurface()
    {
        var exported = typeof(IOnboardingIntake).Assembly
            .GetExportedTypes()
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        exported.Should().BeEquivalentTo(
            ["IOnboardingIntake", "IOnboardingWidgetState", "OnboardingResult", "OnboardingWidgetStep"],
            because: "the review queue, the clear/flag pair and the widget's document resolver have no consumer outside the section");
    }

    private static IEnumerable<Type> SectionTypes() =>
        typeof(OnboardingResource).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.Name.StartsWith('<'));
}
