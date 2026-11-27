using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Humans.Base.Demo;
using Humans.Base.Hosting;
using Humans.Web.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Humans.Web.Tests.Architecture;

/// <summary>
/// Every section that owns data adds its part of the demo world or says why not, and the stories can be ordered.
/// Read off the DI descriptors of a non-production host, without building a provider.
/// </summary>
public class DemoStoryCoverageTests
{
    [HumansFact]
    public void Every_section_with_a_DbContext_has_a_demo_story_or_says_why_not()
    {
        var services = Services();
        var storyAssemblies = StoryTypes(services).Select(t => t.Assembly).ToHashSet();

        var offenders = services.Select(d => d.ImplementationInstance)
            .OfType<SectionDbContextRegistration>()
            .Select(r => r.ContextType.Assembly)
            .Distinct()
            // Shell's SystemDbContext is the platform's (DataProtectionKeys), not a section's.
            .Where(a => a != typeof(Program).Assembly)
            .Where(a => !storyAssemblies.Contains(a)
                && !a.GetCustomAttributes(typeof(NoDemoStoryAttribute), inherit: false).Any())
            .Select(a => a.GetName().Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            "a section that owns data registers an IDemoStory or carries [assembly: NoDemoStory(\"reason\")]");
    }

    [HumansFact]
    public void Demo_stories_wait_only_for_registered_stories_and_never_for_each_other()
    {
        // Key and After are declarations, not state: an uninitialised instance answers them without its services.
        var stories = StoryTypes(Services())
            .Select(t => (IDemoStory)RuntimeHelpers.GetUninitializedObject(t))
            .ToDictionary(s => s.Key);
        stories.Should().NotBeEmpty();

        var done = new HashSet<DemoStoryKey>();
        var remaining = stories.Values.ToList();
        while (remaining.Count > 0)
        {
            var ready = remaining.FirstOrDefault(s => s.After.All(done.Contains));
            ready.Should().NotBeNull(
                $"these stories wait for one that is missing or for each other: {string.Join(", ", remaining.Select(s => s.Key))}");
            done.Add(ready.Key);
            remaining.Remove(ready);
        }
    }

    private static IServiceCollection Services() =>
        new ServiceCollection()
            .AddHumansPersistence()
            .AddDiscoveredSections(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [HostDefaults.EnvironmentKey] = Environments.Staging,
                })
                .Build());

    private static IReadOnlyList<Type> StoryTypes(IServiceCollection services) =>
        [.. services.Where(d => d.ServiceType == typeof(IDemoStory))
            .Select(d => d.ImplementationType)
            .OfType<Type>()];
}
