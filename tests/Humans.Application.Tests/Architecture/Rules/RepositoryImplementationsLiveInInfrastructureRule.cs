using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories.AuditLog;
using Humans.Testing;

namespace Humans.Application.Tests.Architecture.Rules;

/// <summary>
/// Generic rule: every concrete <see cref="IRepository"/> implementation lives
/// in a namespace under <c>Humans.Infrastructure.Repositories.*</c>.
///
/// Repository implementations belong in the Infrastructure layer. A concrete
/// repository in <c>Humans.Application.*</c> or <c>Humans.Web.*</c> is a
/// layer-inversion violation. The Infrastructure assembly is the correct home;
/// the namespace should start with <c>Humans.Infrastructure.Repositories.</c>
/// (with the section-named subfolder as the next segment).
///
/// Reflects over the Infrastructure assembly (via an anchor type) to find
/// every non-abstract class that implements <see cref="IRepository"/> and
/// asserts its namespace is inside the expected prefix.
/// </summary>
public class RepositoryImplementationsLiveInInfrastructureRule
{
    private const string ExpectedNamespacePrefix = "Humans.Infrastructure.Repositories";

    [HumansFact]
    public void All_IRepository_implementations_live_in_Humans_Infrastructure_Repositories()
    {
        var infraAssembly = typeof(AuditLogRepository).Assembly;

        var violations = infraAssembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => typeof(IRepository).IsAssignableFrom(t))
            .Where(t => !(t.Namespace?.StartsWith(ExpectedNamespacePrefix, StringComparison.Ordinal) == true))
            .Select(t => $"{t.FullName} — namespace '{t.Namespace}' is not under '{ExpectedNamespacePrefix}'")
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        violations.Should().BeEmpty(
            because: "repository implementations are Infrastructure-layer concerns; their namespace " +
                     "must start with Humans.Infrastructure.Repositories.* (design-rules §3)");
    }
}
