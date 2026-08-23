using System.Reflection;
using AwesomeAssertions;
using Humans.Monitor.Services;

namespace Humans.Monitor.Tests.Architecture;

/// <summary>
/// The invariants that make Monitor safe to exist. Monitor was carved out of AuditLog because
/// AuditLog is a *horizontal* and two of its actions injected GoogleIntegration services —
/// <c>peters-hard-rules.md</c> forbids a horizontal from referencing a vertical section, and
/// the reference only became visible at the assembly level when GoogleIntegration went to G5.
///
/// Monitor is allowed to reference both because it is the *consumer* end of every edge it has.
/// The load-bearing test is <see cref="SectionReferencesOnlyBaseAndTheLeavesItConsumes"/>: the moment something
/// depends on Monitor, it stops being a leaf and becomes the junk drawer the carve was meant
/// to avoid.
/// </summary>
public class MonitorArchitectureTests
{
    private static Assembly SectionAssembly => typeof(DriveActivityMonitorService).Assembly;

    [HumansFact]
    public void SectionReferencesOnlyBaseAndTheLeavesItConsumes()
    {
        // The reason Monitor exists. It reaches GoogleIntegration *and* AuditLog, which is
        // legal only because Monitor is not a horizontal — AuditLog is, which is why these
        // actions could not stay there (peters-hard-rules.md). The list below is therefore
        // the whole justification for the section, and it is meant to stay short: every name
        // added here is a section Monitor now couples to.
        //
        // Its own outward surface is IDriveActivityMonitorService in Contracts/, consumed by
        // DriveActivityMonitorJob beside it — both folded into this project during the G5
        // jobs move and the Contracts-leaf fold-back (nobodies-collective/Humans#866).
        var sectionRefs = SectionAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("Humans.", StringComparison.Ordinal))
            // Humans.Infrastructure was in this list until G5 lane 5b-6 deleted it.
            .Where(n => n is not ("Humans.Base" or "Humans.Domain" or "Humans.Application"
                                 or "Humans.Analyzers"))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        sectionRefs.Should().BeEquivalentTo(
            [
                // Only AuditAction, for the CheckDriveActivity redirect. Monitor stopped
                // reading audit when SyncAudit.cshtml took over with <vc:audit-log>; the
                // Humans.AuditLog ProjectReference stays for Razor to resolve
                // @addTagHelper *, Humans.AuditLog, but no IL names a type from it, so it is
                // absent here.
                "Humans.AuditLog.Contracts",
                "Humans.GoogleIntegration.Contracts",
                "Humans.Settings.Contracts",
                // Not a fourth coupling — MonitorController and DriveActivityMonitorService have
                // always read IUserServiceRead/UserInfo; the types simply left Humans.Application
                // for Users' contracts leaf (nobodies-collective/Humans#866, lane 2 PR A), which is
                // the first time the dependency shows up as an assembly reference. #866 names
                // User/UserInfo as sanctioned shared contracts and lane 4 settles where they live.
                "Humans.Users.Contracts",
            ],
            because: "Monitor consumes AuditLog's contracts, GoogleIntegration and "
                     + "Settings, plus the sanctioned User/UserInfo contracts, and "
                     + "nothing else");
    }

    [HumansFact]
    public void SectionOwnsNoDbContext()
    {
        // Monitor has no tables. It reads Google through GoogleIntegration and writes
        // through the audit log. A DbContext turning up in any constructor here means
        // the section quietly grew a database, which nothing else would catch.
        var offenders = SectionAssembly
            .GetTypes()
            .SelectMany(t => t.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Select(param => (Type: t, param.ParameterType)))
            .Where(x => x.ParameterType.FullName?.StartsWith("Microsoft.EntityFrameworkCore.", StringComparison.Ordinal) == true)
            .Select(x => $"{x.Type.FullName} takes {x.ParameterType.Name}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "Monitor owns no tables; a DbContext here means a table arrived without a section doc");
    }
}
