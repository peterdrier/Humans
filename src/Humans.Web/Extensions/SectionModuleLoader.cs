using System.Reflection;
using System.Runtime.Loader;

namespace Humans.Web.Extensions;

/// <summary>
/// Loads the section assemblies Shell does not reference. The build copies them, with their package dependencies,
/// beside Shell; they are not in Shell's deps.json, so the runtime resolves them only through this loader. They load
/// into the default context, so <c>Humans.Base</c>, the contracts and every package stay one shared copy.
/// </summary>
/// <remarks>
/// Assemblies resolve by name, lazily, so load order is irrelevant: a module referencing another module resolves it
/// through <see cref="AssemblyLoadContext.Resolving"/> on first use. Anything in Shell's deps.json wins.
/// </remarks>
internal static class SectionModuleLoader
{
    private static readonly Lazy<IReadOnlyList<string>> SectionAssemblyNames = new(() =>
    {
        AssemblyLoadContext.Default.Resolving += Resolve;

        return
        [
            .. Directory.EnumerateFiles(AppContext.BaseDirectory, "Humans.*.dll")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Order(StringComparer.Ordinal)
        ];
    });

    /// <summary>Every <c>Humans.*</c> assembly beside Shell; installs the resolver on first call.</summary>
    internal static IReadOnlyList<string> DiscoveredAssemblyNames() => SectionAssemblyNames.Value;

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Name is null)
        {
            return null;
        }

        var directory = string.IsNullOrEmpty(name.CultureName)
            ? AppContext.BaseDirectory
            : Path.Combine(AppContext.BaseDirectory, name.CultureName);
        var path = Path.Combine(directory, name.Name + ".dll");

        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }
}
