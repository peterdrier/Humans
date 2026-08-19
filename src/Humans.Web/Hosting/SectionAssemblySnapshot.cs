using System.Reflection;
using Humans.Web.Extensions;

namespace Humans.Web.Hosting;

/// <summary>
/// One host's view of the shipped section assemblies, split by what that host's
/// <c>Sections:Active</c> activated. Everything a host composes from discovery —
/// registrations, contributions, policies, jobs, health checks, endpoints, resources and
/// the two MVC feature providers — reads this, so a host composes only its own sections.
/// </summary>
/// <remarks>
/// Per host, deliberately not process state. Several hosts share a process — every
/// <c>WebApplicationFactory</c> in the integration suite builds one — and a shared active
/// set serves whichever host composed first to all of them, registering one host's sections
/// inside another and routing its controllers there (#1081). Built once in <c>Program.cs</c>
/// from that host's configuration, registered as a singleton so post-build composition
/// (recurring jobs) resolves the same instance.
/// </remarks>
internal sealed class SectionAssemblySnapshot
{
    private readonly HashSet<Assembly> _activeLookup;
    private readonly HashSet<Assembly> _inactive;

    /// <param name="shipped">Every section assembly in the build.</param>
    /// <param name="activeSections">
    /// The section names this host activated, or null for the default — every section.
    /// </param>
    internal SectionAssemblySnapshot(IReadOnlyList<Assembly> shipped, IReadOnlySet<string>? activeSections)
    {
        Active = [.. shipped.Where(a => activeSections is null
                                        || activeSections.Contains(SectionDiscoveryExtensions.SectionName(a)))];
        _activeLookup = [.. Active];
        _inactive = [.. shipped.Where(a => !_activeLookup.Contains(a))];
    }

    /// <summary>This host's split of <c>Sections:Active</c>, resolved and validated.</summary>
    internal static SectionAssemblySnapshot For(IConfiguration configuration) =>
        new(SectionDiscoveryExtensions.SectionAssemblies(), SectionActivation.Resolve(configuration));

    /// <summary>The section assemblies this host runs. Composition reads this, never the shipped set.</summary>
    internal IReadOnlyList<Assembly> Active { get; }

    /// <summary>True for a section assembly this host runs — the public check may be relaxed for it.</summary>
    internal bool IsActiveSection(Assembly assembly) => _activeLookup.Contains(assembly);

    /// <summary>
    /// True for a section assembly this host deactivated. MVC's default feature providers
    /// walk every application part, so a deactivated section's <em>public</em> controllers
    /// and view components stay routable unless something takes them back out — and they
    /// then resolve services no <c>Register</c> call ever added.
    /// </summary>
    internal bool IsInactiveSection(Assembly assembly) => _inactive.Contains(assembly);
}
