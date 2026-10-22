using System.Reflection;
using AwesomeAssertions;
using Humans.Web.Extensions;
using Humans.Web.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Humans.Web.Tests.Infrastructure;

/// <summary>
/// MVC's stock <c>IsController</c> requires <c>IsPublic</c>, so an internal section controller builds green and 404s
/// at runtime. Type sweeps like <c>EndpointAuthorizationTests</c> never ask MVC, so this is the only guard. It proves
/// the provider, not that <c>Program.cs</c> registers it.
/// </summary>
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
        // The provider relaxes IsPublic only for assemblies with an ISection entry point; otherwise every internal
        // "*Controller" class anywhere would become routable.
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
