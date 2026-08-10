using AwesomeAssertions;
using Humans.Application.Interfaces.Gdpr;
using Humans.Finance.Contracts;
using Humans.Finance.Controllers;
using Humans.Finance.Data;
using Humans.Finance.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Finance.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Finance
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// Replaces <c>Humans.Application.Tests/Architecture/FinanceArchitectureTests.cs</c>. Its
/// namespace-pinning test is gone — the assembly boundary subsumes it (design §15 step 11) —
/// and so is its "does not reference EF Core" test, which asserted a property of
/// <c>Humans.Application</c> that no longer says anything about this section: the section
/// project references EF Core because its repository lives in it. The rule that matters,
/// "only the repository touches the DbSets", is the universal HUM0025 analyzer's job.
/// </remarks>
public class FinanceArchitectureTests
{
    [HumansFact]
    public void OnlySectionIsPublic()
    {
        // "Public means Section or Contracts/" (design §15 step 5). Finance's cross-section
        // surface is the separate Humans.Finance.Contracts assembly — its two Base consumers
        // (ExpenseReportService, HoldedSyncJob) forced the downward carve — so nothing in *this*
        // assembly needs to be visible outside it, not even a resource marker: the section's
        // views carry no Localizer call sites and carve no .resx.
        //
        // The controller is internal too. Shell registers SectionControllerFeatureProvider,
        // which relaxes MVC's IsPublic check for assemblies carrying [assembly: Section("…")]
        // (memory/architecture/section-controllers-need-feature-provider.md — which says in as
        // many words: do not "fix" a 404 by making the controller public).
        //
        // Generated migration classes are emitted `public partial` by `dotnet ef` and are never
        // hand-edited (memory/process/never-hand-edit-migrations); they are excluded rather
        // than internalized.
        var publicTypes = typeof(Section).Assembly.GetExportedTypes()
            .Where(t => !string.Equals(t.Namespace, "Humans.Finance.Data.Migrations", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        publicTypes.Should().BeEquivalentTo(["Humans.Finance.Section"]);
    }

    [HumansFact]
    public void ContractsExposeOnlyTheCrossSectionSurface()
    {
        // Pins the whole Contracts assembly, so widening Finance's cross-section surface is a
        // visible diff. This is the surface Expenses consumes (nobodies-collective/Humans#866,
        // A3) — changing it changes what A3 compiles against.
        var contractTypes = typeof(IHoldedFinanceService).Assembly.GetExportedTypes()
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        contractTypes.Should().BeEquivalentTo(
        [
            "CreditorBindResult",
            "CreditorContactBinding",
            "CreditorContactSource",
            "CreditorLedgerLine",
            "HoldedActualRow",
            "HoldedContactInfo",
            "HoldedCreditorAccountRow",
            "HoldedCreditorLedger",
            "HoldedCreditorStatus",
            "HoldedDocSyncInfo",
            "HoldedPaymentInfo",
            "HoldedProvisioningPlan",
            "HoldedProvisioningRow",
            "HoldedSyncResult",
            "HoldedUnmatchedRow",
            "IHoldedFinanceService",
        ]);
    }

    [HumansFact]
    public void ContractsDoNotReExposeTheHoldedConnector()
    {
        // The Holded HTTP client is a Base connector with its own docs/sections/Holded.md; it is
        // consumed by Expenses as well as Finance and did not move. Contracts referencing it
        // would cycle (Humans.Application → Humans.Finance.Contracts → Humans.Application), which
        // is exactly why HoldedCreditorLedger.Lines carries Finance's own CreditorLedgerLine
        // instead of the connector's HoldedLedgerLineDto.
        typeof(IHoldedFinanceService).Assembly.GetReferencedAssemblies()
            .Should().NotContain(a => a.Name == "Humans.Application" || a.Name == "Humans.Domain",
                because: "a section's contracts leaf references only the bottom of the graph "
                       + "(memory/architecture/section-project-cycle-fix.md)");
    }

    [HumansFact]
    public void SectionControllersAreInternal()
    {
        var controllers = typeof(Section).Assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Controller", StringComparison.Ordinal))
            .ToList();

        controllers.Should().NotBeEmpty();
        controllers.Should().OnlyContain(t => !t.IsPublic);
    }

    [HumansFact]
    public void FinanceControllerRequiresFinanceAdminOrAdmin()
    {
        // Moved from Humans.Application.Tests' EndpointAuthorizationTests, which sweeps Shell's
        // controllers and can no longer name this one by type.
        typeof(FinanceController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .Single().Policy
            .Should().Be("FinanceAdminOrAdmin");
    }

    [HumansFact]
    public void FinanceControllerKeepsTheFinanceRoutePrefix()
    {
        // The Budget half of this controller stayed in Shell as BudgetAdminController under the
        // same prefix. Both must keep it or URLs move.
        typeof(FinanceController)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>()
            .Single().Template
            .Should().Be("Finance");
    }

    [HumansFact]
    public void ServiceTakesNoCrossSectionRepository()
    {
        var ctor = typeof(Service).GetConstructors().Single();
        ctor.GetParameters()
            .Select(p => p.ParameterType)
            .Where(t => t.Name.EndsWith("Repository", StringComparison.Ordinal)
                     && !string.Equals(t.Name, "IHoldedRepository", StringComparison.Ordinal))
            .Should().BeEmpty();
    }

    [HumansFact]
    public void ServiceImplementsIUserDataContributor()
    {
        typeof(IUserDataContributor).IsAssignableFrom(typeof(Service))
            .Should().BeTrue(
                because: "Finance owns holded_creditor_contacts (user-scoped); it must contribute "
                       + "to the GDPR Article 15 export");
    }

    [HumansFact]
    public void SectionRegistersTheContractAndTheGdprContributorAsTheSameScopedService()
    {
        var services = new ServiceCollection();
        new Section().Register(services, new ConfigurationBuilder().Build());

        services.Single(d => d.ServiceType == typeof(IHoldedRepository)).Lifetime
            .Should().Be(ServiceLifetime.Scoped);
        services.Single(d => d.ServiceType == typeof(IHoldedFinanceService)).Lifetime
            .Should().Be(ServiceLifetime.Scoped);
        services.Should().ContainSingle(d => d.ServiceType == typeof(IUserDataContributor));
    }
}
