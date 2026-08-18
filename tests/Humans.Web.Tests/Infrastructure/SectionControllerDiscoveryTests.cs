using System.Reflection;
using AwesomeAssertions;
using Humans.Web.Extensions;
using Humans.Web.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Humans.Web.Tests.Infrastructure;

/// <summary>
/// Proves that a G5 section's <c>internal</c> controllers are actually discovered by MVC
/// (nobodies-collective/Humans#866).
/// </summary>
/// <remarks>
/// This is the one failure mode in the section split with no other guard: MVC's stock
/// <c>ControllerFeatureProvider.IsController</c> requires <c>IsPublic</c>, so an internal
/// section controller builds green, has its <c>[Route]</c> sitting right there, and 404s at
/// runtime — see <c>memory/architecture/section-controllers-need-feature-provider.md</c>.
/// Store was covered only incidentally, by its 20 controller integration tests; Events has
/// none, so nothing in the suite would have caught a regression here. Sweeps that enumerate
/// controller <em>types</em> (EndpointAuthorizationTests) do not help — they see internal
/// types fine and never ask MVC whether it agrees.
///
/// Runs the real <see cref="SectionControllerFeatureProvider"/> over the real section
/// assemblies, so it covers every section present and future without a per-section edit.
/// It proves the mechanism, not the wiring: that Shell still registers the provider in
/// <c>Program.cs</c> is covered by Store's controller integration tests, which boot the app.
/// </remarks>
public sealed class SectionControllerDiscoveryTests
{
    [HumansFact]
    public void EverySectionControllerIsDiscoveredByMvc()
    {
        var sectionAssemblies = SectionDiscoveryExtensions.SectionAssemblies();
        sectionAssemblies.Should().NotBeEmpty(
            because: "a discovery sweep that finds no section assemblies passes vacuously");

        var declared = sectionAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

        declared.Should().NotBeEmpty(
            because: "the section projects ship controllers; finding none means this sweep is broken");

        var discovered = DiscoverControllers(sectionAssemblies);

        declared.Should().BeSubsetOf(discovered,
            because: "internal section controllers route only because Shell registers "
                   + "SectionControllerFeatureProvider; without it they 404 in silence");
    }

    [HumansFact]
    public void InternalControllersOutsideASectionAreStillNotDiscovered()
    {
        // The provider relaxes IsPublic *only* for assemblies declaring an ISection entry
        // point. If that narrowing ever regressed, every internal class ending in
        // "Controller" anywhere would become routable.
        var provider = new SectionControllerFeatureProvider();
        var feature = new ControllerFeature();

        provider.PopulateFeature(
            [new AssemblyPart(typeof(SectionControllerDiscoveryTests).Assembly)],
            feature);

        feature.Controllers.Should().NotContain(typeof(NotASectionController).GetTypeInfo(),
            because: "this test assembly declares no ISection entry point");
    }

    private static IReadOnlyList<Type> DiscoverControllers(IEnumerable<Assembly> assemblies)
    {
        var provider = new SectionControllerFeatureProvider();
        var feature = new ControllerFeature();

        provider.PopulateFeature(
            [.. assemblies.Select(a => new AssemblyPart(a))],
            feature);

        return [.. feature.Controllers.Select(t => t.AsType())];
    }

    private sealed class NotASectionController : Controller;
}
