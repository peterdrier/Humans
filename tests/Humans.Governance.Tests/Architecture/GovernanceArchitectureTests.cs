using System.Reflection;
using AwesomeAssertions;
using Humans.Governance.Data;
using Humans.Governance.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Humans.Governance.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the repository/service pattern for the Governance section.
/// Governance does not use a caching decorator or in-memory store — the service talks
/// directly to the repository and invalidates cross-cutting caches inline after successful
/// writes.
/// </summary>
public class GovernanceArchitectureTests
{
    /// <summary>
    /// Replaces the pre-G5 assertion that <c>typeof(ApplicationDecisionService).Assembly</c>
    /// carried no EF Core reference. That was a true statement about <c>Humans.Application</c>;
    /// the section assembly holds the repository and references EF on purpose, so the old
    /// assertion would either fail or — read as "does not contain" over a different assembly —
    /// keep passing while asserting nothing (nobodies-collective/Humans#866, Calendar's
    /// finding). The invariant it was reaching for is that the *service* never touches EF, and
    /// the constructor is where that shows.
    /// </summary>
    [HumansFact]
    public void ApplicationDecisionService_TakesNoDbContextOrFactory()
    {
        var ctor = typeof(ApplicationDecisionService).GetConstructors().Single();
        var parameterTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        parameterTypes.Should().NotContain(t => typeof(DbContext).IsAssignableFrom(t),
            because: "services read through IApplicationRepository, never a DbContext (design-rules §1, §3)");
        parameterTypes.Should().NotContain(
            t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IDbContextFactory<>),
            because: "the context factory belongs to the repository, not the service");
    }

    [HumansFact]
    public void ApplicationRepository_IsSealedAndFactoryBased()
    {
        typeof(ApplicationRepository).IsSealed.Should().BeTrue();

        var ctor = typeof(ApplicationRepository).GetConstructors().Single();
        ctor.GetParameters().Should().ContainSingle(
            p => p.ParameterType == typeof(IDbContextFactory<GovernanceDbContext>),
            because: "Governance repositories use IDbContextFactory over their own peeled context (nobodies-collective/Humans#858) so they can be registered as Singleton");
        ctor.GetParameters().Should().NotContain(
            p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
            because: "repositories should not capture scoped DbContext instances");
    }

    /// <summary>
    /// A section RCL's <c>_ViewImports</c> rebinds <c>Localizer</c> for every view in one line,
    /// so a view cannot read a carved key through the wrong set — but a controller that keeps
    /// taking a foreign <see cref="IStringLocalizer{T}"/> compiles and renders its carved keys
    /// as raw key names, usually on a validation path no render fixture reaches (§15.3b).
    /// Governance legitimately binds both markers: the tier-application form's copy and error
    /// messages are rendered by Shell's <c>ProfileController</c> too and stayed in
    /// <c>SharedResource</c>.
    /// </summary>
    [HumansFact]
    public void SectionTypesLocalizeThroughGovernanceOrSharedResourceOnly()
    {
        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IStringLocalizer<>))
            .Select(t => t.GetGenericArguments()[0])
            .Where(t => t != typeof(GovernanceResource) && t != typeof(UI.SharedResource))
            .Distinct()
            .ToList();

        offenders.Should().BeEmpty(
            because: "a section type may only localize through GovernanceResource or the shared set it deliberately still reads");
    }
}
