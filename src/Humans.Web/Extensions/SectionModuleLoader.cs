using System.Reflection;
using System.Runtime.Loader;

namespace Humans.Web.Extensions;

/// <summary>
/// Loads the section assemblies Shell does not reference from <c>modules/</c> beside the app, into the default load
/// context so <c>Humans.Base</c>, the contracts and every package stay one shared copy.
/// </summary>
/// <remarks>
/// Assemblies resolve by name, lazily, so load order is irrelevant: a module referencing another module resolves it
/// through <see cref="AssemblyLoadContext.Resolving"/> on first use. Shell's own output wins over <c>modules/</c>.
/// </remarks>
internal static class SectionModuleLoader
{
    private static readonly string ModulesDirectory = Path.Combine(AppContext.BaseDirectory, "modules");

    private static readonly Lazy<IReadOnlyList<string>> ModuleNames = new(() =>
    {
        if (!Directory.Exists(ModulesDirectory))
        {
            return [];
        }

        AssemblyLoadContext.Default.Resolving += Resolve;

        return
        [
            .. Directory.EnumerateFiles(ModulesDirectory, "Humans.*.dll")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Order(StringComparer.Ordinal)
        ];
    });

    /// <summary>The assembly names found in <c>modules/</c>; installs the resolver on first call.</summary>
    internal static IReadOnlyList<string> DiscoveredModuleNames() => ModuleNames.Value;

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Name is null)
        {
            return null;
        }

        var directory = string.IsNullOrEmpty(name.CultureName)
            ? ModulesDirectory
            : Path.Combine(ModulesDirectory, name.CultureName);
        var path = Path.Combine(directory, name.Name + ".dll");

        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }
}
