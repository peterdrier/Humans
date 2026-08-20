using AwesomeAssertions;

namespace Humans.Debug.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Debug
/// (nobodies-collective/Humans#866, G5).
/// </summary>
public class DebugArchitectureTests
{
    [HumansFact]
    public void SectionTakesNoDbContextOrStore()
    {
        // Debug reads QueryStatistics and TrackingMemoryCache, both in-memory diagnostics
        // singletons. It owns no tables, so nothing here may ask for a DbContext or a
        // repository — the day one appears, this section has quietly grown a data layer.
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

        return type.IsGenericType
            && string.Equals(type.GetGenericTypeDefinition().Name, "IDbContextFactory`1", StringComparison.Ordinal);
    }

    [HumansFact]
    public void SectionRegistersOnlyLogApiWiring()
    {
        // Everything the diagnostics pages read is a singleton some other section already
        // registered. The one thing the section owns is /api/logs' credential filter
        // (nobodies-collective/Humans#1091) — anything beyond that means Debug has grown
        // a service of its own and that is worth a second look rather than a green build.
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        new Section().Register(services, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        var own = services
            .Where(d => d.ServiceType.Assembly == typeof(Section).Assembly)
            .Select(d => d.ServiceType)
            .ToList();

        own.Should().Equal(typeof(LogApiKeyAuthFilter));
    }
}
