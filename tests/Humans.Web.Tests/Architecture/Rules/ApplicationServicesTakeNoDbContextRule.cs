using AwesomeAssertions;
using Humans.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Humans.Web.Tests.Architecture.Rules;

/// <summary>
/// Generic rule: no concrete <see cref="IApplicationService"/> implementation
/// takes <see cref="UsersDbContext"/> or
/// <see cref="IDbContextFactory{TContext}"/> as a constructor parameter.
///
/// Services reach the database exclusively through <see cref="Humans.Application.Interfaces.Repositories.IRepository"/>
/// implementations. A service that directly injects a DbContext bypasses the
/// repository boundary, violating design-rules §3.
///
/// Reflects over the Application assembly (via an anchor type) and over every
/// G5 section assembly to find each non-abstract class in a <c>*.Services</c>
/// namespace, and checks its public constructor parameters. Abstract classes
/// and the repository layer are excluded.
///
/// This rule generalises per-section tests such as
/// <c>AuditLogService_HasNoDbContextConstructorParameter</c> — those can be
/// deleted in Phase 3 once this generic rule is confirmed green.
/// </summary>
public class ApplicationServicesTakeNoDbContextRule
{
    [HumansFact]
    public void Application_services_do_not_take_UsersDbContext()
    {
        // Scan Humans.Application *and* every G5 section assembly, the way the sibling
        // IMemoryCache rule already does. Anchored on Humans.Application alone, this rule kept
        // passing while covering one section fewer at every G5 move — the §10 silent-drop
        // shape, and the same bug Campaigns found in DisplaySortInControllersRule and Email
        // found in NoDestructiveMigrationOpsRule (nobodies-collective/Humans#866). Widening it
        // surfaced no violation, so the cost of being right here was nothing.
        // ApplicationSweepScope holds the anchor and asserts its assembly identity — the anchor
        // has silently drifted out of Humans.Application four times, most recently to
        // Humans.Interfaces, which meant this rule had never once scanned the project it is
        // named for. Same silent-drop the widening below exists to prevent, arriving through
        // the anchor rather than the filter.
        var assemblies = ApplicationSweepScope.Assemblies();

        var violations = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.Namespace?.StartsWith("Humans.Application.Services.", StringComparison.Ordinal) == true
                     || (t.Namespace?.StartsWith("Humans.", StringComparison.Ordinal) == true
                         && t.Namespace.EndsWith(".Services", StringComparison.Ordinal)))
            .SelectMany(t =>
                t.GetConstructors()
                    .SelectMany(c => c.GetParameters())
                    .Where(p => typeof(DbContext).IsAssignableFrom(p.ParameterType)
                                || IsDbContextFactory(p.ParameterType))
                    .Select(p => $"{t.FullName}: ctor param '{p.ParameterType.Name}'"))
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        violations.Should().BeEmpty(
            because: "application services must access the database through IRepository, " +
                     "never by injecting UsersDbContext or IDbContextFactory directly " +
                     "(design-rules §3; §15 Option A/B for caching pattern)");
    }

    private static bool IsDbContextFactory(Type t)
    {
        if (!t.IsGenericType) return false;
        var def = t.GetGenericTypeDefinition();
        return def == typeof(IDbContextFactory<>);
    }
}
