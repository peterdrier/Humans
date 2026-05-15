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
/// Reflects over the Infrastructure assembly (via an anchor type) to find
/// every non-abstract class that implements <see cref="IRepository"/>.
///
/// Abstract base classes are skipped; the rule fires on each leaf
/// implementation individually so failures name the offending class.
/// </summary>
public class IRepositoryImplementationsAreSealedRule
{
    [HumansFact]
    public void All_IRepository_implementations_are_sealed()
    {
        // Anchor: any Infrastructure type gives us the assembly to scan.
        var infraAssembly = typeof(AuditLogRepository).Assembly;

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
