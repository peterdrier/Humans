using System.Net;
using AwesomeAssertions;
using Humans.Budget.Contracts;
using Humans.Budget.Data;
using Humans.Budget.Domain;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders every Budget page through the real app.</summary>
/// <remarks>
/// Failures render as a 200 with degraded content: an unbound <c>&lt;vc:human&gt;</c> ships as markup, a mis-bound key
/// renders as its name, and only Spanish proves the satellites ship.
/// </remarks>
public class BudgetPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    // /Finance is BudgetAdminController's route prefix.
    private static readonly string[] Pages =
    [
        "/Budget",
        "/Budget/Summary",
        "/Finance",
        "/Finance/Admin",
        "/Finance/CashFlow",
        "/Finance/AuditLog",
    ];

    /// <summary>
    /// Without an active year every page renders its no-active-year view, the keys never show, and
    /// <c>/Finance/CashFlow</c> redirects home.
    /// </summary>
    private async Task SeedActiveBudgetYearAsync(CancellationToken ct)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BudgetDbContext>();

        var yearId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();

        db.BudgetYears.Add(new BudgetYear
        {
            Id = yearId,
            Year = "2026",
            Name = "Budget 2026",
            Status = BudgetYearStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.BudgetGroups.Add(new BudgetGroup
        {
            Id = groupId,
            BudgetYearId = yearId,
            Name = "Departments",
            IsDepartmentGroup = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.BudgetCategories.Add(new BudgetCategory
        {
            Id = categoryId,
            BudgetGroupId = groupId,
            Name = "Cantina",
            AllocatedAmount = -1000m,
            ExpenditureType = ExpenditureType.OpEx,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.BudgetLineItems.Add(new BudgetLineItem
        {
            Id = Guid.NewGuid(),
            BudgetCategoryId = categoryId,
            Description = "Food",
            Amount = -250m,
            ExpectedDate = new LocalDate(2026, 6, 1),
            CreatedAt = now,
            UpdatedAt = now,
        });

        await db.SaveChangesAsync(ct);
    }

    [HumansFact(Timeout = 120000)]
    public async Task Every_budget_page_renders_without_raw_resource_keys_or_unbound_tag_helpers()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedActiveBudgetYearAsync(ct);
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        foreach (var url in Pages)
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);

            html.Should().NotContain("Budget_", $"GET {url} rendered a raw resource key");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_budget_summary_page_renders_the_sections_own_copy()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedActiveBudgetYearAsync(ct);
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var html = await (await Client.GetAsync("/Budget/Summary", ct)).Content.ReadAsStringAsync(ct);

        html.Should().Contain("Total Income",
            because: "Budget_TotalIncome resolves through IStringLocalizer<BudgetResource>");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Budget_pages_render_in_spanish_from_the_sections_satellite_assemblies()
    {
        // The neutral set is embedded in the main assembly and fallback is silent, so only Spanish proves the
        // satellites. HtmlEncoder escapes non-ASCII, so the checks stay on ASCII runs.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedActiveBudgetYearAsync(ct);
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // Accept-Language doesn't reach a signed-in user: the PreferredLanguage provider short-circuits the chain. Switch
        // the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Budget/Summary", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var response = await Client.GetAsync("/Budget/Summary", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("Presupuesto");        // Budget_TitleYear
        html.Should().Contain("Ingresos totales");   // Budget_TotalIncome
        html.Should().NotContain("Total Income");
        html.Should().NotContain("Budget_");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_expenses_grid_resolves_budgets_carved_category_key()
    {
        // Expenses' report grid is the one call site outside Budget binding BudgetResource; a miss renders the raw key.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Expenses", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().NotContain("Budget_ColCategory",
            because: "Expenses reads the key through Budget's set, not the ambient shared one");
    }

    private static string? ExtractAntiForgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]{0,200}value=\"(?<token>[^\"]+)\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(2));
        return match.Success ? match.Groups["token"].Value : null;
    }
}
