using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories.AuditLog;

namespace Humans.Application.Tests.Architecture.Rules;

/// <summary>
/// Generic rule: every concrete <see cref="IRepository"/> implementation is
/// <c>sealed</c>.
///
/// Source rule: repository implementations are sealed to prevent ad-hoc
/// extension — any new behavior belongs on the interface, not a subclass.
/// Reflects over the Infrastructure assembly (via an anchor type) *and* every
/// G5 section assembly (nobodies-collective/Humans#866) to find every
/// non-abstract class that implements <see cref="IRepository"/>.
///
/// Abstract base classes are skipped; the rule fires on each leaf
/// implementation individually so failures name the offending class.
/// </summary>
public class IRepositoryImplementationsAreSealedRule
{
    [HumansFact]
    public void All_IRepository_implementations_are_sealed()
    {
        // Scan Humans.Infrastructure *and* every G5 section assembly, the way the sibling
        // ApplicationServicesTakeNoDbContextRule/ApplicationServicesTakeNoMemoryCacheRule
        // rules already do. Anchored on Infrastructure alone, this rule kept passing while
        // covering one section fewer at every G5 move — the §10 silent-drop shape, and the
        // same bug Campaigns found in DisplaySortInControllersRule and Email found in
        // NoDestructiveMigrationOpsRule (nobodies-collective/Humans#866). Widening it
        // surfaced no violation, so the cost of being right here was nothing.
        var infraAssembly = typeof(AuditLogRepository).Assembly;
        var assemblies = new[] { infraAssembly }
            .Concat(Web.Extensions.SectionDiscoveryExtensions.SectionAssemblies());

        var unsealed = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsClass && !t.IsAbstract && !t.IsSealed)
            .Where(t => typeof(IRepository).IsAssignableFrom(t))
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        unsealed.Should().BeEmpty(
            because: "repository implementations are sealed to prevent ad-hoc extension; " +
                     "new behavior belongs on the interface, not a subclass (per design-rules §3)");
    }
}
