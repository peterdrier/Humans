using System.Reflection;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Humans.Base.Hosting;
using Humans.Gdpr.Contracts;
using Humans.Web.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Web.Tests.Services.Gdpr;

/// <summary>
/// Architecture tests for GDPR Article 17 coverage (nobodies-collective/Humans#853).
/// Article 15 export coverage is already enforced by
/// <see cref="GdprExportDependencyInjectionTests"/>; these tests enforce the
/// erasure counterpart, so a section cannot hand a human their data on request
/// and then silently keep it when they ask for deletion.
///
/// <para>
/// The enforcement is the set equality in
/// <see cref="EveryExportSectionHasAnErasureAccount"/>: a section that owns
/// user-scoped tables exports them under a <see cref="GdprExportSections"/>
/// key, and that key must appear in exactly one contributor's
/// <see cref="IUserDataContributor.ErasureDeclaration"/> — either erased
/// (<c>null</c>) or retained with a stated lawful basis. Adding a section
/// without accounting for its erasure fails this test.
/// </para>
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
///
/// <para>
/// All five tests above are set equalities over the two sides of
/// <see cref="GdprExportSections"/> and <see cref="IUserDataContributor"/> — both
/// derived from contributor implementations. A section that owns user-scoped
/// tables but never implemented <see cref="IUserDataContributor"/> is invisible to
/// all of them: it contributes no keys, so the set equality still holds and the
/// section's personal data is never exported and never erased
/// (nobodies-collective/Humans#1116). <see cref="EveryUserScopedSectionHasAContributor"/>
/// closes that gap with an independent inventory: it finds user-scoped tables by
/// reflecting over each section's EF model, not over contributor output, so a
/// section that skipped writing a contributor entirely still gets caught.
/// </para>
/// </summary>
public class GdprErasureCoverageTests
{
    private static readonly IReadOnlyList<IUserDataContributor> Contributors = DiscoverContributors();

    private static readonly IReadOnlySet<string> ExportSectionNames =
        typeof(GdprExportSections)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

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
    public void EveryExportSectionHasAnErasureAccount()
    {
        var declared = Contributors
            .SelectMany(c => c.ErasureDeclaration.Keys)
            .ToHashSet(StringComparer.Ordinal);

        declared.Should().BeEquivalentTo(ExportSectionNames,
            "every category a section exports under Article 15 must be accounted for under Article 17 — " +
            "add the GdprExportSections key to the owning contributor's ErasureDeclaration, mapped to null " +
            "if EraseForUserAsync clears it or to the lawful basis for keeping it");
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

    /// <summary>
    /// Independent check on the same coverage gap as
    /// <see cref="EveryExportSectionHasAnErasureAccount"/>, from the other direction:
    /// instead of trusting contributors to say what they cover, this reflects over
    /// every section's own EF model and asks whether it holds a user-scoped table at
    /// all. A property is treated as a user foreign key when it is a
    /// <see cref="Guid"/> or nullable <see cref="Guid"/> whose name ends in
    /// <c>UserId</c> (<c>CreatedByUserId</c>, <c>ModifiedByUserId</c>, the bare
    /// <c>UserId</c> itself). This deliberately catches the common
    /// "who touched this row" attribution column and misses a user reference
    /// spelled or typed unconventionally (a <c>string</c> email, a differently
    /// named column, a value object wrapping a <see cref="Guid"/>) or one that only
    /// exists as an EF shadow property with no configured name pattern — those need
    /// a human to notice, the same way they would today.
    ///
    /// <para>
    /// Section DbContext types come from <see cref="RegisteredSectionDbContextTypes"/>,
    /// the same DI-registration read <c>DbContextEntityOwnershipTests</c> uses, and
    /// each model is built the same way that test does: against the real Npgsql
    /// provider with a connection string that is never opened, so no database is
    /// contacted. That mechanism is duplicated here rather than shared, because the
    /// two test classes are independent guards that must keep working if either one
    /// is changed or removed on its own.
    /// </para>
    /// </summary>
    [HumansFact]
    public void EveryUserScopedSectionHasAContributor()
    {
        var contextTypes = RegisteredSectionDbContextTypes();
        contextTypes.Should().NotBeEmpty(
            "the guard is meaningless if no section DbContext is discovered");

        var contributorAssemblies = Contributors
            .Select(c => c.GetType().Assembly)
            .ToHashSet();

        var offenders = new List<string>();
        foreach (var contextType in contextTypes.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if (contributorAssemblies.Contains(contextType.Assembly))
                continue;

            var userScopedColumns = UserScopedColumns(contextType);
            if (userScopedColumns.Count == 0)
                continue;

            offenders.Add(
                $"{contextType.Assembly.GetName().Name} ({contextType.Name}) has user-scoped column(s) " +
                $"[{string.Join(", ", userScopedColumns.OrderBy(c => c, StringComparer.Ordinal))}] but no " +
                "IUserDataContributor is registered in that assembly - add one and declare the erasure for " +
                "the section that owns this table");
        }

        // AwesomeAssertions' BeEmpty() reports only the first offending item for an
        // IEnumerable (it stops enumerating once it finds one, to avoid a costly full
        // enumeration on an arbitrary IEnumerable). The whole point of this test is
        // to name every offending section in one failure, so the full list is spelled
        // out in the "because" message rather than left to that default formatting.
        offenders.Should().BeEmpty(
            "a section holding a user foreign key column holds personal data reachable from a specific user, " +
            "so it must account for that data under Article 15/17 even before anyone writes an ErasureDeclaration. " +
            $"Offending sections: {string.Join(" | ", offenders)}");
    }

    /// <summary>
    /// A property counts as a user foreign key by name and type shape alone, never
    /// by section or table name: <c>Guid</c>/<c>Guid?</c> and a name ending in
    /// <c>UserId</c>. See <see cref="EveryUserScopedSectionHasAContributor"/> for
    /// what this does and does not catch.
    /// </summary>
    private static IReadOnlyList<string> UserScopedColumns(Type contextType)
    {
        using var db = CreateSectionDbContext(contextType);

        return
        [
            .. db.Model.GetEntityTypes()
                .SelectMany(entityType => entityType.GetProperties()
                    .Where(p => (p.ClrType == typeof(Guid) || p.ClrType == typeof(Guid?)) &&
                                p.Name.EndsWith("UserId", StringComparison.Ordinal))
                    .Select(p => $"{entityType.ClrType.Name}.{p.Name}")),
        ];
    }

    // Never opened: building the model resolves type mappings, it does not connect.
    // Same connection string DbContextEntityOwnershipTests uses for the same reason.
    private const string DesignTimeConnectionString =
        "Host=localhost;Database=humans_design_time;Username=humans;Password=humans";

    /// <summary>
    /// The authoritative section DbContext list, read the same way
    /// <c>DbContextEntityOwnershipTests.RegisteredContextTypes</c> reads it: off the
    /// <see cref="SectionDbContextRegistration"/> descriptors DI actually produces,
    /// never a hand-maintained list of section or assembly names. A section that
    /// moves projects still shows up here because it still registers its context
    /// the same way.
    /// </summary>
    private static IReadOnlyList<Type> RegisteredSectionDbContextTypes()
    {
        var services = new ServiceCollection()
            .AddHumansPersistence()
            .AddDiscoveredSections(new ConfigurationBuilder().Build());

        return
        [
            .. services.Select(d => d.ImplementationInstance)
                .OfType<SectionDbContextRegistration>()
                .Select(r => r.ContextType)
                .Distinct(),
        ];
    }

    private static DbContext CreateSectionDbContext(Type contextType)
    {
        var optionsBuilder = (DbContextOptionsBuilder)Activator.CreateInstance(
            typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType))!;
        optionsBuilder.UseNpgsql(DesignTimeConnectionString, npgsql => npgsql.UseNodaTime());
        return (DbContext)Activator.CreateInstance(contextType, optionsBuilder.Options)!;
    }
}
