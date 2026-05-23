using AwesomeAssertions;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Regression guard for issue <c>nobodies-collective/Humans#749</c> (and its
/// sibling <c>#742</c>): <see cref="HumansMetricsService"/> must read every
/// gauge value through section services / repositories, never through
/// <c>HumansDbContext</c> directly.
///
/// <para>
/// The metrics service refreshes its snapshot on a 60-second timer by resolving
/// section services from an <c>IServiceScopeFactory</c>. The previous
/// implementation injected a <c>HumansDbContext</c> factory and ran direct
/// group-by / count queries on <c>role_assignments</c> and
/// <c>legal_documents</c> per scrape tick. Those reads now route through
/// <c>IRoleAssignmentService.GetActiveCountsByRoleAsync</c> and
/// <c>ILegalDocumentSyncService.GetActiveRequiredCountAsync</c>. These tests
/// pin the boundary so a future edit can't silently re-introduce direct EF
/// access into the metrics scrape path.
/// </para>
/// </summary>
public class MetricsArchitectureTests
{
    [HumansFact]
    public void HumansMetricsService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(HumansMetricsService).GetConstructors().Single();

        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "the metrics service reads gauge values through section services, not HumansDbContext (nobodies-collective/Humans#749)");
    }

    [HumansFact]
    public void HumansMetricsService_HasNoDbContextFactoryConstructorParameter()
    {
        var ctor = typeof(HumansMetricsService).GetConstructors().Single();

        ctor.GetParameters()
            .Should().NotContain(
                p => p.ParameterType.IsGenericType
                     && p.ParameterType.GetGenericTypeDefinition() == typeof(IDbContextFactory<>),
                because: "the metrics service resolves section services via IServiceScopeFactory, never an IDbContextFactory<HumansDbContext> (nobodies-collective/Humans#749)");
    }

    [HumansFact]
    public void HumansMetricsService_DoesNotReferenceHumansDbContext()
    {
        // Spec wording: "Regression guard asserts HumansMetricsService doesn't
        // reference HumansDbContext." HumansDbContext is internal sealed in
        // Humans.Infrastructure; Humans.Application.Tests sees it via
        // InternalsVisibleTo. Any field or constructor parameter typed as the
        // context (or a factory of it) is a re-introduced direct-EF dependency.
        var serviceType = typeof(HumansMetricsService);

        var fieldTypes = serviceType
            .GetFields(System.Reflection.BindingFlags.Instance
                       | System.Reflection.BindingFlags.NonPublic
                       | System.Reflection.BindingFlags.Public)
            .Select(f => f.FieldType);

        var ctorParamTypes = serviceType.GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType);

        var dependencyTypes = fieldTypes.Concat(ctorParamTypes).ToList();

        dependencyTypes.Should().NotContain(
            t => t == typeof(HumansDbContext)
                 || (t.IsGenericType && t.GetGenericArguments().Contains(typeof(HumansDbContext))),
            because: "the metrics scrape path must stay free of direct HumansDbContext access (nobodies-collective/Humans#749)");
    }
}
