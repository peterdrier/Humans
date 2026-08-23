using System.Net;
using AwesomeAssertions;
using Humans.Budget.Contracts;
using Humans.Budget.Data;
using Humans.Budget.Domain;
using Humans.Expenses.Contracts;
using Humans.Expenses.Data;
using Humans.Expenses.Domain;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders the review queue and the report detail page through the real app, covering the two
/// failure modes that both ship as a <b>200 with degraded content</b> (peterdrier/Humans#1447):
/// an unbound <c>&lt;vc:audit-log&gt;</c>, which the browser drops as inert text, and a resx key
/// the section cannot resolve, which renders as the raw key in every language.
/// </summary>
/// <remarks>
/// The queue used to be two pages, and its decision buttons used to sit on the rows. Both facts
/// are asserted here rather than left to the eye: a queue that grows an Approve button again, or
/// a detail page that loses one, fails.
/// </remarks>
public class ExpensesPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private const string DepartmentName = "Cantina";

    /// <summary>
    /// One active year with a department category, and one report parked in CoordinatorEndorsed
    /// with a cap below its receipts total — the state the whole decision card exists to resolve.
    /// Each integration test class owns its database, so this disturbs nothing.
    /// </summary>
    private async Task<Guid> SeedEndorsedReportAsync(CancellationToken ct)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var scope = Factory.Services.CreateScope();

        var budget = scope.ServiceProvider.GetRequiredService<BudgetDbContext>();
        var yearId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();

        budget.BudgetYears.Add(new BudgetYear
        {
            Id = yearId,
            Year = "2026",
            Name = "Budget 2026",
            Status = BudgetYearStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        });
        budget.BudgetGroups.Add(new BudgetGroup
        {
            Id = groupId,
            BudgetYearId = yearId,
            Name = "Departments",
            IsDepartmentGroup = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        budget.BudgetCategories.Add(new BudgetCategory
        {
            Id = categoryId,
            BudgetGroupId = groupId,
            Name = DepartmentName,
            AllocatedAmount = -5000m,
            ExpenditureType = ExpenditureType.OpEx,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await budget.SaveChangesAsync(ct);

        var expenses = scope.ServiceProvider.GetRequiredService<ExpensesDbContext>();
        var reportId = Guid.NewGuid();
        expenses.ExpenseReports.Add(new ExpenseReport
        {
            Id = reportId,
            SubmitterUserId = Guid.NewGuid(),
            BudgetCategoryId = categoryId,
            BudgetYearId = yearId,
            Status = ExpenseReportStatus.CoordinatorEndorsed,
            Note = "Kitchen gas bottles",
            PayeeName = "Test Person",
            PayeeIban = "ES9121000418450200051332",
            Total = 1300m,
            MaxAmount = 1000m,
            SubmittedAt = now,
            CoordinatorEndorsedAt = now,
            CoordinatorEndorsedByUserId = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
        });
        expenses.ExpenseLines.Add(new ExpenseLine
        {
            Id = Guid.NewGuid(),
            ExpenseReportId = reportId,
            Description = "Gas bottles",
            Amount = 1300m,
            LineType = ExpenseLineType.Receipt,
            SortOrder = 0,
        });
        await expenses.SaveChangesAsync(ct);

        return reportId;
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_review_queue_groups_by_status_and_carries_no_decision_controls()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedEndorsedReportAsync(ct);
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Expenses/Review", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().NotContain("Expenses_", "the queue must resolve its own resource set");

        // The middle crumb is the owning section, not the sidebar's merge group: "Money" holds
        // Expenses, Budget, Finance and Holded, so it never answered "where am I". Scoped to the
        // crumb — the sidebar still groups under "Money", which is its job.
        var crumb = CrumbOf(html);
        crumb.Should().Contain("Expenses").And.Contain("Review");
        crumb.Should().NotContain("Money");

        html.Should().Contain(DepartmentName, "the department column is what the queue is filtered by");
        html.Should().Contain("Coordinator endorsed", "rows are grouped under a localized status heading");

        // Approving from a row meant approving without ever seeing the receipts. The modals that
        // used to live here are the thing being asserted gone, not merely moved.
        html.Should().NotContain("approveModal");
        html.Should().NotContain("rejectModal");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_report_page_carries_the_decision_card_and_the_audit_trail()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var reportId = await SeedEndorsedReportAsync(ct);
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync($"/Expenses/{reportId}", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        // An unrendered <vc:…> is inert text the browser drops, so the card header is the proof
        // the tag helper bound at all.
        html.Should().NotContain("vc:audit-log");
        html.Should().Contain("History");

        html.Should().Contain($"/Expenses/{reportId}/Approve",
            "the approval form is the only place a report can be approved");
        html.Should().Contain("1000.00",
            "the max-amount input is prefilled with the coordinator's cap so a wrong one can be corrected");
        html.Should().Contain(DepartmentName,
            "the category-override select lists real categories rather than asking for a GUID");
    }

    [HumansFact(Timeout = 120000)]
    public async Task A_volunteer_gets_the_queue_in_the_member_shell_scoped_to_their_own_reports()
    {
        // The queue stopped being FinanceAdminOrAdmin-only when the coordinator page folded into
        // it, so a plain member reaches it — in the member shell, seeing only their own rows.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedEndorsedReportAsync(ct);
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var response = await Client.GetAsync("/Expenses/Review", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().NotContain("admin-shell", "a member is not put inside the admin chrome");
        html.Should().NotContain(DepartmentName,
            "someone else's report is not this member's to see");
        html.Should().NotContain("Expenses_");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_review_queue_renders_in_spanish_from_the_sections_satellite_assemblies()
    {
        // An English-only check passes whether or not the RCL's satellites shipped: the neutral
        // set is embedded in the main assembly and the fallback is silent. The queue is only now
        // a page coordinators and members see, so its Spanish copy started mattering.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedEndorsedReportAsync(ct);
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var page = await (await Client.GetAsync("/Expenses/Review", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(page);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token!),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var response = await Client.GetAsync("/Expenses/Review", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain("Departamento");          // Expenses_ColDepartment
        html.Should().Contain("Enviado por");           // Expenses_ColSubmittedBy
        html.Should().NotContain("Expenses_");
    }

    /// <summary>The admin shell's breadcrumb strip — <c>_AdminLayout</c>'s <c>div.crumb</c>.</summary>
    private static string CrumbOf(string html)
    {
        var start = html.IndexOf("class=\"crumb\"", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "the admin shell renders a breadcrumb strip");
        var end = html.IndexOf("</div>", start, StringComparison.Ordinal);
        return html[start..end];
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
