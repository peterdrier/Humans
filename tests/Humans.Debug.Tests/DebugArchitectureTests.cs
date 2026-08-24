using AwesomeAssertions;

namespace Humans.Debug.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Debug
/// (nobodies-collective/Humans#866, G5).
/// </summary>
public class DebugArchitectureTests
{
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
