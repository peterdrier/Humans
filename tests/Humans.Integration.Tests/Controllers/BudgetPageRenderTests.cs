using System.Net;
using AwesomeAssertions;
using Humans.Budget.Contracts;
using Humans.Budget.Data;
using Humans.Budget.Domain;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders every Budget page through the real app, as the standing form of the §15 step 12
/// check for the section's move into <c>src/Sections/Humans.Budget</c>
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// <para>
/// The failure modes a G5 move introduces all render as a <b>200 with degraded content</b>, so
/// "the page loads" is not the assertion:
/// </para>
/// <list type="number">
/// <item><description>
/// A section RCL does not inherit the host's <c>Views/_ViewImports.cshtml</c> — a missing
/// <c>@@using</c> or <c>@@addTagHelper</c> ships literal markup with a green build, and an
/// unrendered <c>&lt;vc:…&gt;</c> element is inert text the browser simply drops.
/// <c>&lt;vc:human&gt;</c> on the audit log is the one Budget uses.
/// </description></item>
/// <item><description>
/// The resx carve moved all 21 <c>Budget_*</c> keys out of <c>SharedResource</c>. A key the
/// carve missed, or a call site bound to the wrong set, renders the raw key — in every
/// language, with no error. The Spanish request is the only thing that proves the section
/// RCL's satellite assemblies reach the host's probing path at all.
/// </description></item>
/// </list>
/// <para>
/// Budget ships no <c>wwwroot/</c>, so there is no static-asset half here.
/// </para>
/// </remarks>
public class BudgetPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    // /Finance is BudgetAdminController's prefix — the URL did not move when the Holded half
    // left for Humans.Finance (A2, peterdrier/Humans#1239).
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
    /// Writes one active year → group → category → line item straight through the section's
    /// own DbContext. Without it every page falls through to its no-active-year view and the
    /// twenty <c>Budget_*</c> keys on Index/Summary — the whole point of the carve check —
    /// never render. <c>/Finance/CashFlow</c> 302s home without it too. Each integration test
    /// class owns its database (nobodies-collective/Humans#983), so this disturbs nothing.
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

            // A view component element that the section's _ViewImports failed to bind renders
            // as literal <vc:…> markup: 200, correct-looking source, nothing on the page.
            html.Should().NotContain("<vc:", $"GET {url} left a view-component tag unrendered");

            // The fallback for a key the carve missed is the key itself.
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
        // An English-only check passes whether or not the RCL's satellites shipped — the neutral
        // set is embedded in the main assembly and the fallback is silent.
        // Razor's default HtmlEncoder escapes non-ASCII to numeric entities, so the assertions
        // stay on ASCII-only runs of the Spanish copy.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedActiveBudgetYearAsync(ct);
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // Accept-Language does not reach a signed-in user: Program.cs's initial culture provider
        // returns the user's PreferredLanguage and short-circuits the rest of the chain, and
        // every Budget page is [Authorize]. Switch the language the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Budget/Summary", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token!),
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
        // Budget_ColCategory went home to BudgetResource at Budget's G5 rather than staying in
        // SharedResource, because both owners are now sections (§15.3b). Expenses' report grid
        // is the one call site outside Budget; it binds IStringLocalizer<BudgetResource>, and a
        // miss there renders the raw key with a green build.
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
