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
        // Its own outward surface is Humans.Monitor.Contracts — one interface, one method,
        // consumed by DriveActivityMonitorJob in Base (G5-SECTION-TEMPLATE.md step 6b).
        var sectionRefs = SectionAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("Humans.", StringComparison.Ordinal))
            .Where(n => n is not ("Humans.Interfaces" or "Humans.Domain" or "Humans.Application"
                                 or "Humans.Infrastructure" or "Humans.UI" or "Humans.Analyzers"
                                 or "Humans.Monitor.Contracts"))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        sectionRefs.Should().BeEquivalentTo(
            ["Humans.AuditLog.Contracts", "Humans.SystemSettings.Contracts"],
            because: "Monitor consumes AuditLog and SystemSettings through their leaves; "
                     + "GoogleIntegration is still Base-resident and arrives via Humans.Application");
    }

    [HumansFact]
    public void SectionTypesTakeNoStringLocalizer()
    {
        // Monitor ships no Resources/ folder and no MonitorResource: its one page is admin-only
        // English (G5-SECTION-TEMPLATE.md step 3b's first question, answered "no keys"). Assert
        // it structurally so the day someone adds copy, the build says "carve a resource set".
        var offenders = SectionAssembly
            .GetTypes()
            .SelectMany(t => t.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Concat(t.GetMethods().SelectMany(m => m.GetParameters()))
                .Select(param => (Type: t, param.ParameterType)))
            .Where(x => x.ParameterType.IsGenericType
                        && string.Equals(
                            x.ParameterType.GetGenericTypeDefinition().FullName,
                            "Microsoft.Extensions.Localization.IStringLocalizer`1",
                            StringComparison.Ordinal))
            .Select(x => $"{x.Type.FullName} takes {x.ParameterType.Name}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "Monitor has no resource set; a localizer here means copy was added without carving one");
    }

    [HumansFact]
    public void SectionOwnsNoDbContext()
    {
        // Monitor owns no tables — it reads Google through GoogleIntegration's connector
        // abstraction and writes through IAuditLogService. No DbContext, no repository, no
        // AddSectionDbContext (template step 1's table-less shape).
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
