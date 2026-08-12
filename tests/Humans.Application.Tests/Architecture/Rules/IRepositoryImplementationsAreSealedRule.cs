using Humans.Infrastructure.Hosting;
using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;

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
        // Anchor: any Infrastructure type gives us the assembly to scan — but it must be one
        // that stays there. AuditLogRepository was the anchor until its own G5 move, which
        // would have silently repointed "the Infrastructure assembly" at a section.
        // Widened to the section assemblies too: a repository that moves out of
        // Humans.Infrastructure must not stop being swept (G5-SECTION-TEMPLATE.md step 11).
        var assemblies = new[] { typeof(InfrastructureServiceCollectionExtensions).Assembly }
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
