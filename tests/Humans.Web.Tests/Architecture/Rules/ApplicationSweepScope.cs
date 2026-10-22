using System.Reflection;

namespace Humans.Web.Tests.Architecture.Rules;

/// <summary>
/// The one assembly set the generic service rules sweep. A per-rule <c>typeof(...).Assembly</c> anchor drifted
/// silently as types moved, keeping the rules green while they covered less.
/// </summary>
internal static class ApplicationSweepScope
{
    public static IEnumerable<Assembly> Assemblies() =>
        Extensions.SectionDiscoveryExtensions.SectionAssemblies();
}
