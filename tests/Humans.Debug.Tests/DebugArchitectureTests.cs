using AwesomeAssertions;
using Humans.UI;
using Microsoft.Extensions.Localization;

namespace Humans.Debug.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Debug
/// (nobodies-collective/Humans#866, G5).
/// </summary>
public class DebugArchitectureTests
{


    [HumansFact]
    public void SectionTypesBindOnlyTheSharedResourceSet()
    {
        // Surveys' guard in Governance's two-marker form, degenerate to one marker — and the
        // marker is SharedResource rather than a DebugResource, which is the whole point.
        // Debug carved no keys because it has none; the single localizer it touches is the
        // shared set read as *data* by /Debug/Translations, which renders every key in every
        // culture as a coverage gallery. Without this, the day someone adds page copy they
        // would reach for the ambient shared set exactly as ConsentController did, and a
        // controller-resolved string renders the raw key past a green render test
        // (§15 step 3b).
        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Concat(t.GetMethods().SelectMany(m => m.GetParameters()))
                .Where(p => p.ParameterType.IsGenericType
                            && p.ParameterType.GetGenericTypeDefinition() == typeof(IStringLocalizer<>)
                            && p.ParameterType.GetGenericArguments()[0] != typeof(SharedResource))
                .Select(_ => t.FullName ?? t.Name))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "Debug has no resource set of its own; /Debug/Translations reads "
                   + "SharedResource as data and nothing here may bind a third set");
    }

    [HumansFact]
    public void SectionTakesNoDbContextOrStore()
    {
        // Calendar's rule (§15 step 11): the assembly-level "does not reference
        // Microsoft.EntityFrameworkCore" assertion cannot survive a section that references
        // Humans.Infrastructure — it would keep passing while asserting nothing. Restated on
        // the constructors, which is what the original was reaching for. Debug reads
        // QueryStatistics and TrackingMemoryCache, both in-memory diagnostics singletons; it
        // must never acquire a DbContext.
        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Where(p => IsForbiddenDataDependency(p.ParameterType))
                .Select(p => $"{t.FullName}({p.ParameterType.Name})"))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "Debug owns no tables and reads diagnostics through Application interfaces");
    }

    private static bool IsForbiddenDataDependency(Type type)
    {
        if (type.Name.EndsWith("DbContext", StringComparison.Ordinal)
            || type.Name.EndsWith("Repository", StringComparison.Ordinal))
            return true;

        if (type.IsGenericType
            && string.Equals(type.GetGenericTypeDefinition().Name, "IDbContextFactory`1", StringComparison.Ordinal))
            return true;

        return string.Equals(type.Namespace, "Humans.Application.Interfaces.Stores", StringComparison.Ordinal);
    }

    [HumansFact]
    public void SectionRegistersNothing()
    {
        // Scanner's shape, pinned rather than assumed: every dependency the diagnostics pages
        // read is a Base singleton registered by its owner. If a later change adds a
        // registration here, that is a signal the section has grown a service and this test is
        // the place to reconsider it — not a failure to route around.
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        new Section().Register(services, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        services.Should().BeEmpty();
    }
}
