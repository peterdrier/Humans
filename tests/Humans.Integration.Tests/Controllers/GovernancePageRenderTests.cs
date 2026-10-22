using Humans.Governance.Contracts;
using System.Net;
using AwesomeAssertions;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using MemberApplication = Humans.Governance.Domain.Application;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders every Governance page through the real app.</summary>
/// <remarks>
/// Failures render as a 200 with degraded content: unbound tags ship as markup, and a mis-bound key renders as its
/// name. <c>ProfileCard</c> and <c>AccessMatrix</c> are invoked by name and throw instead. Some prefixes stay shared
/// because Shell renders them too, so the checks assert the carved prefixes are gone and the shared ones resolve.
/// </remarks>
public class GovernancePageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private const string SeededMotivation = "I have run the build crew shower project for three years.";
    private const string SeededContribution = "Rebuilt the greywater system in 2025.";

    /// <summary>
    /// Written straight to the context: no caching decorator fronts <c>ApplicationDecisionService</c>. Without a row the
    /// list pages render empty and most keys never show.
    /// </summary>
    private async Task<Guid> SeedApplicationAsync(Guid userId, CancellationToken ct)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GovernanceDbContext>();

        var applicationId = Guid.NewGuid();
        db.Applications.Add(new MemberApplication
        {
            Id = applicationId,
            UserId = userId,
            MembershipTier = MembershipTier.Asociado,
            Motivation = SeededMotivation,
            SignificantContribution = SeededContribution,
            RoleUnderstanding = "Asociados vote at the assembly and elect the board.",
            Language = "en",
            SubmittedAt = now,
            UpdatedAt = now,
        });
        db.BoardVotes.Add(new BoardVote
        {
            Id = Guid.NewGuid(),
            ApplicationId = applicationId,
            BoardMemberUserId = Guid.NewGuid(),
            Vote = VoteChoice.Yay,
            Note = "Strong contributor.",
            VotedAt = now,
        });

        await db.SaveChangesAsync(ct);
        return applicationId;
    }

    private static readonly string[] CarvedPrefixes =
    [
        "Governance_", "Governance_Create_", "Governance_Application_", "Governance_BoardVoting_",
        "Governance_ApplicationDetail_", "Governance_AdminAppDetail_", "Governance_AdminApplicationDetail_", "Governance_AdminApplications_",
        "Governance_MyApplications_"
    ];

    private static void AssertRenderedCleanly(string html, string what)
    {
        html.Should().NotContain("-view-component", $"{what} has a renamed view-component element");
        foreach (var prefix in CarvedPrefixes)
        {
            html.Should().NotContain(prefix, $"{what} rendered a raw {prefix}* resource key");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_governance_overview_renders_its_copy_and_the_access_matrix_widget()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var response = await Client.GetAsync("/Governance", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        AssertRenderedCleanly(html, "GET /Governance");

        html.Should().Contain("Who Should Apply?");            // Governance_WhoShouldApply
        // An unbound <vc:> ships as inert markup, so the emitted modal id is the proof, not the 200.
        html.Should().Contain("sectionHelp-Governance",
            because: "the access-matrix component renders a modal id built from the section key; "
                   + "an unbound tag helper renders nothing at all");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_my_applications_list_and_detail_render_without_raw_keys()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);
        var applicationId = await SeedApplicationAsync(userId, ct);

        var list = await Client.GetAsync("/Governance/Applications", ct);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var listHtml = await list.Content.ReadAsStringAsync(ct);
        AssertRenderedCleanly(listHtml, "GET /Governance/Applications");
        listHtml.Should().Contain("My Applications");                 // Governance_MyApplications_Title
        listHtml.Should().NotContain("ApplicationStatus_",
            "the ApplicationStatus_* keys stayed shared and must still resolve");

        var detail = await Client.GetAsync($"/Governance/Applications/Details/{applicationId}", ct);
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        var detailHtml = await detail.Content.ReadAsStringAsync(ct);
        AssertRenderedCleanly(detailHtml, "GET /Governance/Applications/Details/{id}");
        detailHtml.Should().Contain("Application Information");       // Governance_ApplicationDetail_ApplicationInfo
        detailHtml.Should().Contain(SeededMotivation);
        // These partials resolve by name: a miss throws, and the wrong resource set renders raw keys.
        detailHtml.Should().Contain(SeededContribution);
        detailHtml.Should().NotContain("Application_",
            "the Application_* keys stayed shared and must still resolve");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_new_application_form_renders_its_shared_and_carved_copy()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var response = await Client.GetAsync("/Governance/Applications/Create", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        AssertRenderedCleanly(html, "GET /Governance/Applications/Create");

        html.Should().Contain("New Application");   // Governance_Create_NewApplication (carved)
        // The tier radios and Asociado labels stay in SharedResource and bind SharedLocalizer.
        html.Should().NotContain("ProfileEdit_");
        html.Should().NotContain("Profile_MembershipTier");
        html.Should().NotContain("Application_");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_admin_list_and_detail_render_the_moved_shell_partials()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var applicantId = Guid.NewGuid();
        var applicationId = await SeedApplicationAsync(applicantId, ct);
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var list = await Client.GetAsync("/Governance/Applications/Admin", ct);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var listHtml = await list.Content.ReadAsStringAsync(ct);
        AssertRenderedCleanly(listHtml, "GET /Governance/Applications/Admin");
        listHtml.Should().Contain("Asociado Applications");   // Governance_AdminApplications_Title
        // _ApplicationsListContent is resolved by name across application parts.
        listHtml.Should().Contain("Applicant");               // AdminApp_Applicant, in the partial
        listHtml.Should().NotContain("AdminApp_",
            "AdminApp_Applicant stayed shared and must still resolve");

        var detail = await Client.GetAsync($"/Governance/Applications/Admin/{applicationId}", ct);
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        var detailHtml = await detail.Content.ReadAsStringAsync(ct);
        AssertRenderedCleanly(detailHtml, "GET /Governance/Applications/Admin/{id}");
        detailHtml.Should().Contain("Review Application");    // Governance_AdminApplicationDetail_Title
        detailHtml.Should().Contain(SeededMotivation);
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_board_voting_dashboard_and_detail_render_the_profile_card()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var applicantId = Guid.NewGuid();
        var applicationId = await SeedApplicationAsync(applicantId, ct);
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var dashboard = await Client.GetAsync("/Governance/BoardVoting", ct);
        dashboard.StatusCode.Should().Be(HttpStatusCode.OK);
        var dashboardHtml = await dashboard.Content.ReadAsStringAsync(ct);
        AssertRenderedCleanly(dashboardHtml, "GET /Governance/BoardVoting");
        dashboardHtml.Should().Contain("Board Voting Dashboard");   // Governance_BoardVoting_Title

        var detail = await Client.GetAsync($"/Governance/BoardVoting/{applicationId}", ct);
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        var detailHtml = await detail.Content.ReadAsStringAsync(ct);
        AssertRenderedCleanly(detailHtml, "GET /Governance/BoardVoting/{id}");
        detailHtml.Should().Contain("Votes So Far");                // Governance_BoardVoting_VotesSoFar
        // ProfileCard is invoked by name and throws if unresolvable, so reaching this is the proof.
        detailHtml.Should().Contain("Strong contributor.");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Governance_pages_render_in_spanish_from_the_sections_satellite_assemblies()
    {
        // The neutral set is embedded in the main assembly and fallback is silent, so only Spanish proves the
        // satellites. HtmlEncoder escapes non-ASCII, so the checks stay on ASCII runs.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        // Accept-Language doesn't reach a signed-in user: the PreferredLanguage provider short-circuits the chain. Switch
        // the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Governance", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var response = await Client.GetAsync("/Governance", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        AssertRenderedCleanly(html, "GET /Governance (es)");

        html.Should().Contain("Gobernanza");                   // Governance_Title
        html.Should().NotContain("Who Should Apply?");
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
