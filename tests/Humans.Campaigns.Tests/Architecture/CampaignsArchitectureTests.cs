using AwesomeAssertions;
using Humans.Campaigns.Contracts;
using Humans.Campaigns.Data;
using Humans.Campaigns.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Humans.Campaigns.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the section shape for Campaigns
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// Replaces <c>Humans.Application.Tests/Architecture/CampaignsArchitectureTests.cs</c>. Its
/// <c>CampaignService_DoesNotReferenceEntityFrameworkCore</c> test is gone: it asserted that
/// <c>Humans.Application</c> carries no EF reference, and the section assembly holds the
/// repository and legitimately does — so over there the assertion is either false or vacuous.
/// The invariant it was reaching for (the service never touches a <c>DbContext</c>) is
/// asserted on the constructor instead, which is stronger and survives the move.
/// </remarks>
public class CampaignsArchitectureTests
{
    [HumansFact]
    public void OnlySectionIsPublic()
    {
        // "Public means Section or Contracts/" (design §15 step 5), and Campaigns ships no
        // resource set at all — its views carry no Localizer[…] call and SharedResource has no
        // Campaign_ key — so there is no CampaignsResource marker to except (§15 step 3b;
        // Finance and Gate are the other two).
        //
        // CampaignController is internal. Shell registers SectionControllerFeatureProvider,
        // which relaxes MVC's IsPublic check for assemblies carrying [assembly: Section("…")]
        // (memory/architecture/section-controllers-need-feature-provider.md — which says in as
        // many words: do not "fix" a 404 by making the controller public).
        //
        // Generated migration classes are emitted `public partial` by `dotnet ef` and are never
        // hand-edited (memory/process/never-hand-edit-migrations); they are excluded rather
        // than internalized.
        var publicTypes = typeof(Section).Assembly.GetExportedTypes()
            .Where(t => !string.Equals(t.Namespace, "Humans.Campaigns.Data.Migrations", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        publicTypes.Should().BeEquivalentTo(["Humans.Campaigns.Section"]);
    }

    [HumansFact]
    public void SectionControllersAreInternal()
    {
        var controllers = typeof(Section).Assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Controller", StringComparison.Ordinal))
            .ToList();

        controllers.Should().ContainSingle();
        controllers.Should().OnlyContain(t => !t.IsPublic);
    }

    [HumansFact]
    public void ControllerKeepsItsRoutePrefix()
    {
        // Every /Campaigns/Admin URL is unchanged — a G5 move changes files, never routes.
        var type = typeof(Section).Assembly
            .GetType("Humans.Campaigns.Controllers.CampaignController", throwOnError: true)!;

        type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), inherit: false)
            .Cast<Microsoft.AspNetCore.Mvc.RouteAttribute>()
            .Single().Template
            .Should().Be("Campaigns/Admin");
    }

    [HumansFact]
    public void CampaignService_ConstructorTakesNoEfType()
    {
        var parameterTypes = typeof(CampaignService).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToList();

        parameterTypes.Should().NotContain(t => typeof(DbContext).IsAssignableFrom(t),
            because: "the service goes through ICampaignRepository; only the repository owns a DbContext");
        parameterTypes.Should().NotContain(
            t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IDbContextFactory<>),
            because: "context lifetime is the repository's business (design-rules §3)");
    }

    [HumansFact]
    public void CampaignRepository_UsesDbContextFactory()
    {
        var ctor = typeof(CampaignRepository).GetConstructors().Single();
        ctor.GetParameters()
            .Should().ContainSingle(
                p => p.ParameterType == typeof(IDbContextFactory<CampaignsDbContext>),
                because: "the repository is registered as singleton and must create scoped contexts through its own peeled context's factory (nobodies-collective/Humans#858)");
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "repository should not capture scoped DbContext instances");
    }

    /// <summary>
    /// Pins the set of types that may inject <see cref="ICampaignRepository"/>: the owning
    /// service and the repository implementation. A new consumer taking the repository directly
    /// would bypass the service layer and the single-writer rule for the <c>campaign*</c> tables.
    /// </summary>
    [HumansFact]
    public void ICampaignRepository_HasNoUnexpectedConsumers()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Humans.Campaigns.Services.CampaignService",
            "Humans.Campaigns.Data.CampaignRepository",
        };

        var consumers = typeof(Section).Assembly.GetTypes()
            .Where(t => t.GetConstructors()
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(ICampaignRepository))))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        consumers.Where(c => !allowed.Contains(c)).Should().BeEmpty(
            because: "every read/write to the campaign* tables must go through CampaignService");
    }

    [HumansFact]
    public void CampaignServiceContracts_ExposeNoEntityTypes()
    {
        var entityNamespace = typeof(Domain.Campaign).Namespace;

        var offenders = typeof(ICampaignService).GetMethods()
            .Concat(typeof(ICampaignServiceRead).GetMethods())
            .Where(m => NamesAnEntity(m.ReturnType)
                     || m.GetParameters().Any(p => NamesAnEntity(p.ParameterType)))
            .Select(m => m.Name)
            .ToList();

        offenders.Should().BeEmpty(
            because: "the contracts leaf is DTO-only; EF entities stay inside the section");

        bool NamesAnEntity(Type type) =>
            string.Equals(type.Namespace, entityNamespace, StringComparison.Ordinal)
            || (type.IsGenericType && type.GetGenericArguments().Any(NamesAnEntity));
    }

    [HumansFact]
    public void SectionTypesTakeNoStringLocalizer()
    {
        // Campaigns has no resource set (§15 step 3b). This is the structural version of that
        // decision: the day someone adds copy, the build says "carve a resource set first"
        // rather than resolving a Campaign_ key against SharedResource and rendering the raw
        // key in every language. Gate ships the same guard for the same reason.
        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors().SelectMany(c => c.GetParameters()
                .Where(p => p.ParameterType.IsGenericType
                         && p.ParameterType.GetGenericTypeDefinition() == typeof(IStringLocalizer<>))
                .Select(p => $"{t.FullName} takes IStringLocalizer<{p.ParameterType.GetGenericArguments()[0].Name}>")))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "a section with no Resources/ folder cannot localize anything; carve a "
                   + "CampaignsResource set first (§15 step 3b)");
    }
}
