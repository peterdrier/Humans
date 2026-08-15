using Humans.Infrastructure.Hosting;
using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;

namespace Humans.Application.Tests.Architecture.Rules;

/// <summary>
/// Generic rule: every concrete <see cref="IRepository"/> implementation still in
/// <c>Humans.Infrastructure</c> lives in a namespace under
/// <c>Humans.Infrastructure.Repositories.*</c>, with the section-named subfolder as
/// the next segment (design-rules §3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope is Humans.Infrastructure only.</b> A repository that has moved into its
/// section project (#866, G5) lives under that section's own <c>Data/</c> namespace
/// by construction — the assembly boundary is the location rule there, and asserting
/// a <c>Humans.Infrastructure.*</c> prefix over section assemblies would be actively
/// wrong. Nothing else covers the nine repositories still in
/// <c>src/Humans.Infrastructure/Repositories/</c>: HUM0013 constrains repository
/// <i>interfaces</i> in <c>Humans.Application</c>, and HUM0034 does not run outside a
/// section assembly.
/// </para>
/// <para>
/// This rule retires itself alongside
/// <see cref="IRepositoryImplementationsAreSealedRule"/>: once
/// <c>src/Humans.Infrastructure/Repositories/</c> is empty, delete the file.
/// </para>
/// </remarks>
public class RepositoryImplementationsLiveInInfrastructureRule
{
    private const string ExpectedNamespacePrefix = "Humans.Infrastructure.Repositories";

    [HumansFact]
    public void All_IRepository_implementations_live_in_Humans_Infrastructure_Repositories()
    {
        // Anchor must be a type that stays in Humans.Infrastructure — AuditLogRepository was
        // the anchor until its own G5 move (see IRepositoryImplementationsAreSealedRule).
        // G5 lane 3a-2 (nobodies-collective/Humans#866) split AddSectionDbContext out of this
        // type into Humans.Interfaces (Base) under the same namespace, so assert the identity
        // rather than trusting the name.
        var infraAssembly = typeof(InfrastructureServiceCollectionExtensions).Assembly;
        infraAssembly.GetName().Name.Should().Be("Humans.Infrastructure",
            because: "an anchor whose type leaves this assembly would silently retarget the sweep and pass vacuously instead of failing");

        var violations = infraAssembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => typeof(IRepository).IsAssignableFrom(t))
            .Where(t => !IsUnderExpectedNamespace(t.Namespace))
            .Select(t => $"{t.FullName} — namespace '{t.Namespace}' is not under '{ExpectedNamespacePrefix}'")
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        violations.Should().BeEmpty(
            because: "repository implementations are Infrastructure-layer concerns; their namespace " +
                     "must start with Humans.Infrastructure.Repositories.* (design-rules §3)");
    }

    /// <summary>
    /// Matches the prefix on a namespace-segment boundary, not on raw characters: a bare
    /// <c>StartsWith</c> would also accept <c>Humans.Infrastructure.RepositoriesLegacy</c>,
    /// which is a different namespace and exactly the drift this rule exists to catch.
    /// </summary>
    private static bool IsUnderExpectedNamespace(string? ns) =>
        string.Equals(ns, ExpectedNamespacePrefix, StringComparison.Ordinal)
        || ns?.StartsWith(ExpectedNamespacePrefix + ".", StringComparison.Ordinal) == true;
}
