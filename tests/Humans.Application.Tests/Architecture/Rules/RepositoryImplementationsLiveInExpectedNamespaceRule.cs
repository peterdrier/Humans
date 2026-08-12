using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories.AuditLog;

namespace Humans.Application.Tests.Architecture.Rules;

/// <summary>
/// Generic rule: every concrete <see cref="IRepository"/> implementation lives in the
/// namespace its own assembly requires — <c>Humans.Infrastructure.Repositories.*</c> for a
/// repository in the Infrastructure assembly, or <c>Humans.&lt;Section&gt;.Data</c> for a
/// repository in a G5 section assembly (nobodies-collective/Humans#866).
///
/// Repository implementations belong in the Infrastructure layer, or — once a section has
/// moved into its own project — in that section's own <c>Data</c> namespace. A concrete
/// repository anywhere else (e.g. <c>Humans.Application.*</c> or <c>Humans.Web.*</c>, or a
/// section repository sitting under <c>Humans.Infrastructure.Repositories.*</c> instead of
/// its own <c>Data</c> namespace) is a layer-inversion violation.
///
/// Reflects over the Infrastructure assembly (via an anchor type) *and* every G5 section
/// assembly to find every non-abstract class that implements <see cref="IRepository"/>, and
/// asserts its namespace matches the prefix its own assembly requires — derived from the
/// assembly itself, never a hardcoded section list.
/// </summary>
public class RepositoryImplementationsLiveInExpectedNamespaceRule
{
    private const string InfrastructureNamespacePrefix = "Humans.Infrastructure.Repositories";

    [HumansFact]
    public void All_IRepository_implementations_live_in_their_expected_namespace()
    {
        // Scan Humans.Infrastructure *and* every G5 section assembly, the way the sibling
        // ApplicationServicesTakeNoDbContextRule/ApplicationServicesTakeNoMemoryCacheRule
        // rules already do. Anchored on Infrastructure alone, this rule kept passing while
        // covering one section fewer at every G5 move — the §10 silent-drop shape — and its
        // old assertion was also architecturally wrong for a section repository, which
        // correctly lives in Humans.<Section>.Data, not Humans.Infrastructure.Repositories.*
        // (nobodies-collective/Humans#866).
        var infraAssembly = typeof(AuditLogRepository).Assembly;
        var assemblies = new[] { infraAssembly }
            .Concat(Web.Extensions.SectionDiscoveryExtensions.SectionAssemblies());

        var violations = assemblies
            .SelectMany(a => a.GetTypes().Select(t => (Assembly: a, Type: t)))
            .Where(x => x.Type.IsClass && !x.Type.IsAbstract)
            .Where(x => typeof(IRepository).IsAssignableFrom(x.Type))
            .Select(x => (
                x.Type,
                ExpectedPrefix: x.Assembly == infraAssembly
                    ? InfrastructureNamespacePrefix
                    : $"{x.Assembly.GetName().Name}.Data"))
            .Where(x => !(x.Type.Namespace?.StartsWith(x.ExpectedPrefix, StringComparison.Ordinal) == true))
            .Select(x => $"{x.Type.FullName} — namespace '{x.Type.Namespace}' is not under '{x.ExpectedPrefix}'")
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        violations.Should().BeEmpty(
            because: "repository implementations belong in Infrastructure " +
                     "(Humans.Infrastructure.Repositories.*) or, for a G5 section, in that " +
                     "section's own Humans.<Section>.Data namespace (design-rules §3)");
    }
}
