using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Humans.Gdpr.Contracts;

namespace Humans.Web.Tests.Services.Gdpr;

/// <summary>
/// Architecture tests for GDPR Article 17 coverage (nobodies-collective/Humans#853).
/// Every exported section having an erasure declaration is enforced at runtime by
/// <see cref="Humans.Gdpr.Services.GdprService.ExportForUserAsync"/> (it throws if a
/// contributor exports a key its own <see cref="IUserDataContributor.ErasureDeclaration"/>
/// doesn't cover); these tests cover what that per-contributor check can't: duplicate
/// claims and undocumented retention across the whole roster.
///
/// <para>
/// Contributors are found by reflection over the same section assemblies the
/// runtime composes itself from, never a pinned list of type or assembly
/// names, so a section that moves or renames cannot drop out of the sweep.
/// Declarations are read from <see cref="RuntimeHelpers.GetUninitializedObject"/>
/// instances: that costs nothing, needs no database, and means an
/// implementation that reaches for instance state, the DbContext or the clock
/// fails here rather than in production.
/// </para>
/// </summary>
public class GdprErasureCoverageTests
{
    private static readonly IReadOnlyList<IUserDataContributor> Contributors = DiscoverContributors();

    /// <summary>
    /// Reflects over the section assemblies the host composes itself from
    /// (<c>SectionDiscoveryExtensions</c>) plus the host assembly, and
    /// materializes one constructor-free instance of every
    /// <see cref="IUserDataContributor"/> implementation. Nothing here names a
    /// section, a path, or an assembly string.
    /// </summary>
    private static IReadOnlyList<IUserDataContributor> DiscoverContributors()
    {
        // SectionAssemblies(), not ActiveSectionAssemblies(): deliberately the stricter
        // sweep. A section switched off still ships its tables, so its erasure has to be
        // declared — turning a section off must not turn its Article 17 account off too.
        var hostAssembly = typeof(Extensions.InfrastructureServiceCollectionExtensions).Assembly;

        return Extensions.SectionDiscoveryExtensions.SectionAssemblies()
            .Append(hostAssembly)
            .Distinct()
            .SelectMany(asm => asm.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => typeof(IUserDataContributor).IsAssignableFrom(t))
            .Distinct()
            .Select(t => (IUserDataContributor)RuntimeHelpers.GetUninitializedObject(t))
            .ToArray();
    }

    [HumansFact]
    public void ContributorsAreDiscoverable()
    {
        // Guards the rest of the class against passing vacuously if section
        // discovery ever returns nothing.
        Contributors.Should().NotBeEmpty(
            "GDPR erasure coverage is enforced by reflecting over the composed section assemblies");
    }

    [HumansFact]
    public void NoTwoContributorsClaimTheSameSection()
    {
        var duplicates = Contributors
            .SelectMany(c => c.ErasureDeclaration.Keys.Select(k => (Key: k, Owner: c.GetType().Name)))
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} claimed by {string.Join(", ", g.Select(x => x.Owner))}")
            .ToArray();

        duplicates.Should().BeEmpty(
            "one section owns each export category, so exactly one contributor may declare its erasure");
    }

    [HumansFact]
    public void EveryContributorDeclaresAtLeastOneSection()
    {
        foreach (var contributor in Contributors)
        {
            contributor.ErasureDeclaration.Should().NotBeEmpty(
                $"{contributor.GetType().Name} implements IUserDataContributor, so it holds user data and must " +
                "say what erasure does to it");
        }
    }

    [HumansFact]
    public void ExactlyOneContributorErasesLast()
    {
        // GdprService.EraseForUserAsync orders erasure by ErasesLast so the Account owner
        // (the identity every other contributor may need to resolve) runs after everyone
        // else. Two contributors claiming it would make erasure order nondeterministic
        // between them; zero would erase the account first, before sections that still
        // need it.
        var erasesLast = Contributors.Where(c => c.ErasesLast).ToArray();

        erasesLast.Should().ContainSingle(
            "GdprService orders erasure by ErasesLast, so exactly one contributor may run last");
    }

    [HumansFact]
    public void EveryRetainedCategoryStatesItsLawfulBasis()
    {
        // A non-null value means "this survives erasure". Article 17(3) only
        // permits that with a reason, and the reason has to be legible to the
        // next person who reads the declaration — an empty or one-word string
        // is an undocumented retention, which is the failure this catches.
        const int MinimumReasonLength = 20;

        foreach (var contributor in Contributors)
        {
            foreach (var (section, retention) in contributor.ErasureDeclaration)
            {
                if (retention is null)
                {
                    continue;
                }

                retention.Trim().Length.Should().BeGreaterThanOrEqualTo(MinimumReasonLength,
                    $"{contributor.GetType().Name} keeps {section} after erasure, so the declaration must name " +
                    "what survives and the lawful basis for keeping it");
            }
        }
    }
}
