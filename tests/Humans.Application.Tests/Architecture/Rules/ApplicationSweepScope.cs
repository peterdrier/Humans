using System.Reflection;
using AwesomeAssertions;
using Humans.Application.Services.Dashboard;

namespace Humans.Application.Tests.Architecture.Rules;

/// <summary>
/// The assembly set the generic Application service rules sweep: Humans.Application itself,
/// reached through an anchor type, plus every G5 section assembly.
/// </summary>
/// <remarks>
/// <para>
/// The anchor keeps escaping. <c>AuditLogService</c> was the original; when it moved to its own
/// section project, <c>typeof(...).Assembly</c> silently became that section's assembly and the
/// rules stopped scanning Humans.Application at all. Its replacement, <c>DontFixAttribute</c>,
/// had already left for <c>Humans.Interfaces</c> by the time it was written, so the rules named
/// for Humans.Application had in fact never scanned it. Four such silent anchor drifts have now
/// happened in this repo (nobodies-collective/Humans#866), every one of them keeping the test
/// green while covering less — the §10 silent-drop shape.
/// </para>
/// <para>
/// So the anchor lives here once rather than once per rule, and <see cref="Assemblies"/> asserts
/// the identity it depends on instead of assuming it. A future move of
/// <see cref="DashboardService"/> fails loudly and names its own fix.
/// </para>
/// </remarks>
internal static class ApplicationSweepScope
{
    /// <summary>
    /// Humans.Application (via <see cref="DashboardService"/>) followed by every section assembly.
    /// </summary>
    public static IEnumerable<Assembly> Assemblies()
    {
        var anchor = typeof(DashboardService).Assembly;

        anchor.GetName().Name.Should().Be(
            "Humans.Application",
            because: "these rules exist to sweep Humans.Application; if the anchor type has moved " +
                     "out, they would silently scan the section it moved to instead. Re-anchor on a " +
                     "type that is still in Humans.Application");

        return new[] { anchor }
            .Concat(Web.Extensions.SectionDiscoveryExtensions.SectionAssemblies());
    }
}
