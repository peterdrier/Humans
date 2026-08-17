using System.Reflection;

namespace Humans.Application.Tests.Architecture.Rules;

/// <summary>
/// The assembly set the generic Application service rules sweep: every G5 section assembly.
/// </summary>
/// <remarks>
/// <para>
/// The anchor kept escaping. <c>AuditLogService</c> was the original; when it moved to its own
/// section project, <c>typeof(...).Assembly</c> silently became that section's assembly and the
/// rules stopped scanning Humans.Application at all. Its replacement, <c>DontFixAttribute</c>,
/// had already left for <c>Humans.Interfaces</c> by the time it was written, so the rules named
/// for Humans.Application had in fact never scanned it. Four such silent anchor drifts happened
/// (nobodies-collective/Humans#866), every one keeping the test green while covering less.
/// </para>
/// <para>
/// COVERAGE REDUCED (G5 lane 5c): the Humans.Application half of this set is gone, because the
/// assembly holds no types. Two services left the sweep with it — <c>DashboardService</c> and
/// <c>AdminDashboardService</c>, now <c>Humans.Web.Services.Dashboard</c>, which is neither a
/// section assembly nor a <c>*.Services</c> namespace the rules' filter matches. Restoring them
/// means widening the filter, which the batch's ruling 1 defers to a post-migration pass.
/// </para>
/// </remarks>
internal static class ApplicationSweepScope
{
    /// <summary>Every G5 section assembly, via the same discovery the runtime uses.</summary>
    public static IEnumerable<Assembly> Assemblies() =>
        Web.Extensions.SectionDiscoveryExtensions.SectionAssemblies();
}
