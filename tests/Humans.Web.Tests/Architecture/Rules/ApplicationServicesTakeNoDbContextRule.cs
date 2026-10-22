using AwesomeAssertions;
using Humans.Base.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Humans.Web.Tests.Architecture.Rules;

/// <summary>Services reach the database only through repositories (design-rules §3).</summary>
public class ApplicationServicesTakeNoDbContextRule
{
    [HumansFact]
    public void Application_services_do_not_take_UsersDbContext()
    {
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
