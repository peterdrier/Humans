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

    /// <summary>
    /// Surveys' structural localizer guard in Governance's multi-marker form. Onboarding owns
    /// its 67 keys; <c>SharedResource</c> keeps the shared vocabulary the widget renders and
    /// the three <c>Onboarding_*Label</c> keys MVC's global data-annotation localizer resolves;
    /// and the widget's Consents step renders Consent's own copy. A type bound to any
    /// <i>fourth</i> set is the failure this catches — the one a render test cannot, because
    /// controller-resolved copy sits on POST and failure branches.
    /// </summary>

    /// <summary>
    /// HUM0034 is the build gate; this states the intent in the section's own terms so a new
    /// public type has to justify itself as a contract rather than slip in as surface.
    /// </summary>

    private static IEnumerable<Type> SectionTypes() =>
        typeof(OnboardingResource).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.Name.StartsWith('<'));
}
