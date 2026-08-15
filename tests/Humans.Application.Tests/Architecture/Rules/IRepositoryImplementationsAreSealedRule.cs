using Humans.Infrastructure.Hosting;
using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;

namespace Humans.Application.Tests.Architecture.Rules;

/// <summary>
/// Generic rule: every concrete <see cref="IRepository"/> implementation in
/// <c>Humans.Infrastructure</c> is <c>sealed</c>.
///
/// Source rule: repository implementations are sealed to prevent ad-hoc
/// extension — any new behavior belongs on the interface, not a subclass.
///
/// Abstract base classes are skipped; the rule fires on each leaf
/// implementation individually so failures name the offending class.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope is Humans.Infrastructure only, deliberately.</b> In a G5 section
/// assembly the invariant is structural: HUM0034 makes every non-<c>Contracts/</c>
/// type <c>internal</c>, and MA0053 ("make class or record sealed") then errors on
/// any unsealed <c>internal</c> class — so a section repository cannot be unsealed
/// and still compile. Sweeping the section assemblies here would re-assert what the
/// build already guarantees (memory: retirement-first for subsumed guardrails).
/// </para>
/// <para>
/// <c>Humans.Infrastructure</c> is not a section assembly, so HUM0034 does not run
/// there and nothing forces its repositories <c>internal</c>. MA0053 does not report
/// <c>public</c> classes (sealing one is a breaking change), so
/// <c>public class FooRepository : IFooRepository</c> in
/// <c>src/Humans.Infrastructure/Repositories/</c> compiles clean and unsealed —
/// verified against this tree. That is the gap this rule covers, for the nine
/// repositories still living there.
/// </para>
/// <para>
/// This rule retires itself: when the last repository leaves
/// <c>src/Humans.Infrastructure/Repositories/</c> for its section project (#866, G5),
/// the sweep finds nothing and the file should be deleted rather than repointed.
/// </para>
/// </remarks>
public class IRepositoryImplementationsAreSealedRule
{
    [HumansFact]
    public void All_IRepository_implementations_are_sealed()
    {
        // Anchor: any Infrastructure type gives us the assembly to scan — but it must be one
        // that stays there. AuditLogRepository was the anchor until its own G5 move, which
        // would have silently repointed "the Infrastructure assembly" at a section.
        // G5 lane 3a-2 (nobodies-collective/Humans#866) took AddSectionDbContext out of this
        // type and into Humans.Interfaces (Base) under the *same namespace*, so the name below
        // now resolves against two assemblies' worth of namespace — hence the identity check.
        var infraAssembly = typeof(InfrastructureServiceCollectionExtensions).Assembly;
        infraAssembly.GetName().Name.Should().Be("Humans.Infrastructure",
            because: "an anchor whose type leaves this assembly would silently retarget the sweep and pass vacuously instead of failing");

        var unsealed = infraAssembly
            .GetTypes()
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
