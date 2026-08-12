using AwesomeAssertions;
using Humans.Gdpr.Contracts;
using Humans.Gdpr.Services;
using Microsoft.Extensions.Localization;

namespace Humans.Gdpr.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Gdpr
/// (nobodies-collective/Humans#866, G5). The section had no architecture test file
/// before the move — its G0 audit recorded the missing invariants doc as predicate 7's
/// only gap and left the shape untested — so these are new with the project.
/// </summary>
public class GdprArchitectureTests
{
    [HumansFact]
    public void OnlySectionIsPublic()
    {
        // "Public means Section, <Section>Resource or Contracts/" (design §15 step 5),
        // enforced at build time by HUM0034. Gdpr's whole outward surface lives on the
        // separate Humans.Gdpr.Contracts leaf, it owns no tables so there are no
        // migrations, and it ships no resource set — so Section is the only public type
        // in the section assembly.
        var publicTypes = typeof(Section).Assembly.GetExportedTypes()
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        publicTypes.Should().BeEquivalentTo(["Humans.Gdpr.Section"]);
    }

    [HumansFact]
    public void SectionHasNoControllers()
    {
        // The two download actions stay on Shell's ProfileController and GuestController —
        // moving either would be a URL change, out of a G5 move's scope. Stated as a test
        // rather than left to read as an oversight: this is why the project is plain
        // Microsoft.NET.Sdk, and adding a controller here means adding Sdk.Razor and a
        // Views/ tree at the same time.
        typeof(Section).Assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Controller", StringComparison.Ordinal))
            .Should().BeEmpty();
    }

    [HumansFact]
    public void SectionTypesTakeNoStringLocalizer()
    {
        // Gate's strict form of the step 3b guard: Gdpr has no Resources/ folder, no
        // GdprResource and no Gdpr_* key anywhere, so *any* IStringLocalizer<T> here would
        // be a type reaching for the ambient shared set an RCL cannot see. The section is
        // where this matters least visibly and therefore most — it renders nothing, so a
        // contributor adding copy has nothing in the section to copy from. Sweeps method
        // parameters as well as constructor ones (Debug's rule: the injection can be
        // [FromServices] on an action).
        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors()
                    .SelectMany(c => c.GetParameters())
                    .Concat(t.GetMethods().SelectMany(m => m.GetParameters()))
                    .Where(p => p.ParameterType.IsGenericType
                                && p.ParameterType.GetGenericTypeDefinition() == typeof(IStringLocalizer<>))
                    .Select(_ => t.FullName ?? t.Name))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "Gdpr ships no resource set; the day someone adds copy the build should "
                   + "tell them to carve one rather than resolving against SharedResource");
    }

    [HumansFact]
    public void OrchestratorTakesNoRepositoryDbContextOrStore()
    {
        // Calendar's rule (§15 step 11): the assembly-level
        // GetReferencedAssemblies().NotContain("Microsoft.EntityFrameworkCore") assertion
        // is restated on the constructor, which is what it was always reaching for. Gdpr is
        // an orchestrator — the hard rules say orchestrators do not call repositories — so
        // the assertion is about the layer, not just about EF: the fan-out sees other
        // sections' data only through IUserDataContributor.
        var parameters = typeof(GdprExportService).GetConstructors().Single().GetParameters();

        parameters.Should().Contain(p => p.ParameterType == typeof(IEnumerable<IUserDataContributor>));
        parameters.Should().NotContain(
            p => p.ParameterType.Name.EndsWith("Repository", StringComparison.Ordinal)
                 || p.ParameterType.Name.EndsWith("DbContext", StringComparison.Ordinal)
                 || p.ParameterType.Name.StartsWith("IDbContextFactory", StringComparison.Ordinal),
            because: "Gdpr owns no tables and orchestrators do not call repositories "
                   + "(peters-hard-rules.md); every read goes through a contributor");
    }

    [HumansFact]
    public void SectionRegistersOnlyTheOrchestrator()
    {
        // The contributor forwarding factories belong to the sections that own the
        // contributors and stay registered beside them — Section.Register here is not a
        // parking lot (§15 step 4, Governance's rule), and it could not name thirteen
        // sections' internal service types anyway.
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        new Section().Register(services, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        services.Should().ContainSingle();
        services.Should().ContainSingle(d => d.ServiceType == typeof(IGdprExportService));
    }
}
