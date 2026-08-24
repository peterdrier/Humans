using AwesomeAssertions;

namespace Humans.Debug.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Debug
/// (nobodies-collective/Humans#866, G5).
/// </summary>
public class DebugArchitectureTests
{
    [HumansFact]
    public void SectionRegistersNothingOfItsOwn()
    {
        // Everything the diagnostics pages read is a singleton some other section already
        // registered, and the one thing Debug used to own — /api/logs' credential filter —
        // moved to Humans.Backdoor with the rest of the machine surface
        // (nobodies-collective/Humans#1128). Anything appearing here means Debug has grown
        // a service of its own and that is worth a second look rather than a green build.
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        new Section().Register(services, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        services
            .Where(d => d.ServiceType.Assembly == typeof(Section).Assembly)
            .Should().BeEmpty();
    }
}
