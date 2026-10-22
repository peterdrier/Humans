using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Humans.Gdpr.Contracts;

namespace Humans.Web.Tests.Services.Gdpr;

/// <summary>
/// GDPR Article 17 checks across the whole roster: <c>GdprService.ExportForUserAsync</c> already throws on an
/// uncovered key per contributor, so these cover duplicate claims and undocumented retention.
/// </summary>
/// <remarks>
/// Declarations are read off <see cref="RuntimeHelpers.GetUninitializedObject"/> instances, so one that reaches for
/// instance state, the DbContext or the clock fails here rather than in production.
/// </remarks>
public class GdprErasureCoverageTests
{
    private static readonly IReadOnlyList<IUserDataContributor> Contributors = DiscoverContributors();

    private static IReadOnlyList<IUserDataContributor> DiscoverContributors()
    {
        // SectionAssemblies(), not ActiveSectionAssemblies(): a section switched off still ships its tables, so it
        // still has to declare its erasure.
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
        // ErasesLast runs the Account owner after everyone who still needs to resolve the identity. Two make the
        // order nondeterministic; none erases the account first.
        var erasesLast = Contributors.Where(c => c.ErasesLast).ToArray();

        erasesLast.Should().ContainSingle(
            "GdprService orders erasure by ErasesLast, so exactly one contributor may run last");
    }

    [HumansFact]
    public void EveryRetainedCategoryStatesItsLawfulBasis()
    {
        // A non-null value means the data survives erasure, which Article 17(3) allows only with a legible reason.
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
