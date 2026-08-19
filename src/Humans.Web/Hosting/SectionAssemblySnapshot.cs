using System.Reflection;
using Humans.Web.Extensions;

namespace Humans.Web.Hosting;

/// <summary>
/// One host's view of the shipped section assemblies, split by what that host's
/// <c>Sections:Active</c> activated. Handed to the MVC feature providers, which see one
/// type at a time and would otherwise re-walk the dependency graph per type.
/// </summary>
/// <remarks>
/// Per host, deliberately not a static cache. Several hosts share a process — every
/// <c>WebApplicationFactory</c> in the integration suite builds one — and a static set
/// freezes whichever host composed first, so a later host with a different allowlist would
/// route the first host's controllers and resolve its view components. The rest of
/// discovery re-reads <see cref="SectionDiscoveryExtensions.ActiveSectionAssemblies"/> per
/// call and is unaffected; these two lookups are the only cached ones (#1081).
/// </remarks>
internal sealed class SectionAssemblySnapshot
{
    private readonly HashSet<Assembly> _active;
    private readonly HashSet<Assembly> _inactive;

    /// <param name="shipped">Every section assembly in the build.</param>
    /// <param name="activeSections">
    /// The section names this host activated, or null for the default — every section.
    /// </param>
    internal SectionAssemblySnapshot(IReadOnlyList<Assembly> shipped, IReadOnlySet<string>? activeSections)
    {
        _active = [.. shipped.Where(a => activeSections is null
                                         || activeSections.Contains(SectionDiscoveryExtensions.SectionName(a)))];
        _inactive = [.. shipped.Where(a => !_active.Contains(a))];
    }

    /// <summary>True for a section assembly this host runs — the public check may be relaxed for it.</summary>
    internal bool IsActiveSection(Assembly assembly) => _active.Contains(assembly);

    /// <summary>
    /// True for a section assembly this host deactivated. MVC's default feature providers
    /// walk every application part, so a deactivated section's <em>public</em> controllers
    /// and view components stay routable unless something takes them back out — and they
    /// then resolve services no <c>Register</c> call ever added.
    /// </summary>
    internal bool IsInactiveSection(Assembly assembly) => _inactive.Contains(assembly);
}
