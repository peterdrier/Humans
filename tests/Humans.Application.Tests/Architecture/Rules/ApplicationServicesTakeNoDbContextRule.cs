using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Services.AuditLog;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Humans.Application.Tests.Architecture.Rules;

/// <summary>
/// Generic rule: no concrete <see cref="IApplicationService"/> implementation
/// takes <see cref="HumansDbContext"/> or
/// <see cref="IDbContextFactory{TContext}"/> as a constructor parameter.
///
/// Services reach the database exclusively through <see cref="Humans.Application.Interfaces.Repositories.IRepository"/>
/// implementations. A service that directly injects a DbContext bypasses the
/// repository boundary, violating design-rules §3.
///
/// Reflects over the Application assembly (via an anchor type) to find every
/// non-abstract class whose namespace starts with
/// <c>Humans.Application.Services.</c> and checks its public constructor
/// parameters. Abstract classes and the repository layer are excluded.
///
/// This rule generalises per-section tests such as
/// <c>AuditLogService_HasNoDbContextConstructorParameter</c> — those can be
/// deleted in Phase 3 once this generic rule is confirmed green.
/// </summary>
public class ApplicationServicesTakeNoDbContextRule
{
    [HumansFact]
    public void Application_services_do_not_take_HumansDbContext()
    {
        // Anchor: any Application type gives us the assembly to scan.
        var appAssembly = typeof(AuditLogService).Assembly;

        var violations = appAssembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.Namespace?.StartsWith("Humans.Application.Services.", StringComparison.Ordinal) == true)
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
                     "never by injecting HumansDbContext or IDbContextFactory directly " +
                     "(design-rules §3; §15 Option A/B for caching pattern)");
    }

    private static bool IsDbContextFactory(Type t)
    {
        if (!t.IsGenericType) return false;
        var def = t.GetGenericTypeDefinition();
        return def == typeof(IDbContextFactory<>);
    }
}
