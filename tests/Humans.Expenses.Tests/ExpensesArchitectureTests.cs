using AwesomeAssertions;
using Humans.Gdpr.Contracts;
using Humans.Expenses.Contracts;
using Humans.Expenses.Controllers;
using Humans.Expenses.Data;
using Humans.Expenses.Domain;
using Humans.Expenses.Services;
using Humans.UI.Models.Tables;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Expenses.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Expenses
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// Replaces <c>Humans.Application.Tests/Architecture/ExpensesArchitectureTests.cs</c>. Both of
/// its tests are gone rather than carried: the namespace-pinning one is subsumed by the assembly
/// boundary, and "does not reference EF Core" asserted a property of <c>Humans.Application</c>
/// that says nothing about this section — the section project references EF Core because its
/// repository lives in it. The rule that still matters, "only the repository touches the DbSets",
/// is the universal HUM0025 analyzer's job (design §15 step 11).
/// </remarks>
public class ExpensesArchitectureTests
{
    [HumansFact]
    public void OnlySectionAndResourceArePublic()
    {
        // "Public means Section or Contracts/" (design §15 step 5). ExpensesResource is the one
        // sanctioned extra: the boot localization diagnostic discovers section resource markers
        // through GetExportedTypes(), so an internal marker is skipped in silence (§15 step 3b).
        //
        // The controller is internal. Shell registers SectionControllerFeatureProvider, which
        // relaxes MVC's IsPublic check for assemblies carrying [assembly: Section("…")]
        // (memory/architecture/section-controllers-need-feature-provider.md — which says in as
        // many words: do not "fix" a 404 by making the controller public).
        //
        // Generated migration classes are emitted `public partial` by `dotnet ef` and are never
        // hand-edited (memory/process/never-hand-edit-migrations); they are excluded rather
        // than internalized.
        var publicTypes = typeof(Section).Assembly.GetExportedTypes()
            .Where(t => !string.Equals(t.Namespace, "Humans.Expenses.Data.Migrations", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        publicTypes.Should().BeEquivalentTo(
        [
            "Humans.Expenses.ExpensesResource",
            "Humans.Expenses.Section",
        ]);
    }

    [HumansFact]
    public void ContractsExposeOnlyTheCrossSectionSurface()
    {
        // Pins the whole Contracts assembly, so widening Expenses' cross-section surface is a
        // visible diff rather than a silent one. IExpenseReportBackgroundProcessor's only
        // consumer outside the section that is not in Shell is HoldedExpenseOutboxJob, which
        // stays in Humans.Infrastructure because recurring jobs have no discovery seam yet
        // (§15 step 6b). IExpenseReportServiceRead — and the DTO graph it returns
        // (ExpenseReportDto/ExpenseLineDto/ExpenseAttachmentDto/ExpenseHoldedTimeline/
        // ExpenseAttachmentDownload) plus the two enums that graph exposes
        // (ExpenseReportStatus/ExpenseLineType) — were promoted for the admin dashboard tile
        // (nobodies-collective/Humans#1264 tile wave, Peter-approved).
        var contractTypes = typeof(IExpenseReportBackgroundProcessor).Assembly.GetExportedTypes()
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        contractTypes.Should().BeEquivalentTo(
        [
            "IExpenseReportBackgroundProcessor",
            "IExpenseReportServiceRead",
            "ExpenseReportDto",
            "ExpenseLineDto",
            "ExpenseAttachmentDto",
            "ExpenseAttachmentDownload",
            "ExpenseHoldedTimeline",
            "ExpenseReportStatus",
            "ExpenseLineType",
        ]);
    }

    [HumansFact]
    public void ContractsDoNotReExposeTheHoldedConnector()
    {
        // ExpenseReportService.DrainHoldedOutboxAsync uses IHoldedClient,
        // HoldedPurchaseDocumentInput, HoldedAttachmentInput and HoldedTransientException
        // heavily. Those belong to the Base connector, which did not move
        // (memory/architecture/vendor-connectors-own-sections.md). Letting one into the
        // contracts leaf is the cycle Finance hit in A2 with HoldedLedgerLineDto.
        typeof(IExpenseReportBackgroundProcessor).Assembly.GetReferencedAssemblies()
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
    public void ExpensesControllerKeepsTheExpensesRoutePrefix()
    {
        typeof(ExpensesController)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>()
            .Single().Template
            .Should().Be("Expenses");
    }

    [HumansFact]
    public void ServiceTakesNoCrossSectionRepository()
    {
        var ctor = typeof(ExpenseReportService).GetConstructors().Single();
        ctor.GetParameters()
            .Select(p => p.ParameterType)
            .Where(t => t.Name.EndsWith("Repository", StringComparison.Ordinal)
                     && !string.Equals(t.Name, "IExpenseRepository", StringComparison.Ordinal))
            .Should().BeEmpty();
    }

    [HumansFact]
    public void ServiceImplementsIUserDataContributor()
    {
        typeof(IUserDataContributor).IsAssignableFrom(typeof(ExpenseReportService))
            .Should().BeTrue(
                because: "Expenses owns expense_reports (user-scoped); it must contribute to the "
                       + "GDPR Article 15 export");
    }

    [HumansFact]
    public void SectionRegistersTheContractAndTheGdprContributorAsTheSameScopedService()
    {
        var services = new ServiceCollection();
        new Section().Register(services, new ConfigurationBuilder().Build());

        services.Single(d => d.ServiceType == typeof(IExpenseRepository)).Lifetime
            .Should().Be(ServiceLifetime.Singleton);
        services.Single(d => d.ServiceType == typeof(IExpenseReportServiceRead)).Lifetime
            .Should().Be(ServiceLifetime.Scoped);
        services.Single(d => d.ServiceType == typeof(IExpenseReportBackgroundProcessor)).Lifetime
            .Should().Be(ServiceLifetime.Scoped);
        services.Should().ContainSingle(d => d.ServiceType == typeof(IUserDataContributor));
    }

    [HumansFact]
    public void SectionRegistersABadgeClassForEveryExpenseReportStatus()
    {
        // Humans.UI cannot name ExpenseReportStatus any more, so the badge rows moved here
        // (Peter, 2026-08-09 — memory/architecture/base-ui-registries-are-section-populated.md).
        // An unregistered value does not fail: EnumBadgeMap.For falls back to "bg-secondary" and
        // the badge renders grey, which no build, suite or HTML diff would flag — and two of
        // these five legitimately *are* bg-secondary, so "not the fallback" cannot be the
        // assertion. The five classes are pinned by value instead, exactly as Humans.UI's literal
        // held them before the move.
        new Section().Register(new ServiceCollection(), new ConfigurationBuilder().Build());

        var actual = Enum.GetValues<ExpenseReportStatus>()
            .ToDictionary(s => s.ToString(), s => EnumBadgeMap.For(s), StringComparer.Ordinal);

        actual.Should().BeEquivalentTo(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Draft"] = "bg-secondary",
            ["Submitted"] = "bg-primary",
            ["CoordinatorEndorsed"] = "bg-info text-dark",
            ["Approved"] = "bg-success",
            ["Withdrawn"] = "bg-secondary",
        });
    }
}
