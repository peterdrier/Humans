using Humans.Users.Contracts;
using System.Net;
using AwesomeAssertions;
using Humans.Governance.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
// Humans.Application is a sibling namespace inside this project, so the bare name
// `Application` resolves to it rather than to the entity.
using MemberApplication = Humans.Governance.Domain.Application;
using Humans.Domain.Enums;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders every Governance page through the real app, as the standing form of the §15 step 12
/// check for the section's move into <c>src/Sections/Humans.Governance</c>
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// <para>
/// The failure modes a G5 move introduces all render as a <b>200 with degraded content</b>:
/// </para>
/// <list type="number">
/// <item><description>
/// A section RCL does not inherit the host's <c>Views/_ViewImports.cshtml</c> — a missing
/// <c>@@using</c> or <c>@@addTagHelper</c> ships literal markup with a green build, and an
/// unrendered <c>&lt;vc:…&gt;</c> element is inert text the browser drops. Governance renders
/// <c>&lt;vc:human&gt;</c> on four pages, and two components it cannot bind at all:
/// <c>ProfileCard</c> and <c>AccessMatrix</c> both live in Shell and are invoked by name across
/// application parts, which <b>throws</b> when it fails rather than degrading.
/// </description></item>
/// <item><description>
/// The resx carve moved 112 keys across nine prefixes out of <c>SharedResource</c>. A key the
/// carve missed, or a call site left bound to the wrong set, renders the raw key in every
/// language with no error. Four prefixes deliberately stayed shared (<c>Application_*</c>,
/// <c>ApplicationStatus_*</c>, <c>AdminApp_Applicant</c> and the Profile / OnboardingReview
/// singles) because Shell's <c>ProfileController</c> and the onboarding review queue render
/// them too — so the assertions below check the carved prefixes are gone <i>and</i> that the
/// shared ones still resolve.
/// </description></item>
/// <item><description>
/// Three Shell partials moved in with the section (<c>_ApplicationsListContent</c>,
/// <c>_ApplicationResponseSections</c>, <c>_ApplicationHistory</c>) and are now reached by
/// name rather than by <c>~/Views/Shared/…</c> path.
/// </description></item>
/// </list>
/// <para>
/// Governance ships no <c>wwwroot/</c>, so there is no static-asset half here.
/// </para>
/// </remarks>
public class GovernancePageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private const string SeededMotivation = "I have run the build crew shower project for three years.";
    private const string SeededContribution = "Rebuilt the greywater system in 2025.";

    /// <summary>
    /// Writes one Submitted application straight through the section's own DbContext. There is
    /// no §15 caching decorator in front of <c>ApplicationDecisionService</c> — it caches only
    /// the per-board-member voting badge — so the context write is what every read below sees.
    /// Without a row, <c>/Governance/Applications</c>, the admin list and the voting dashboard
    /// all render their empty views and most of the carved keys never render at all. Each
    /// integration test class owns its database (nobodies-collective/Humans#983).
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

    /// <summary>Every prefix the carve took into <c>GovernanceResource</c>.</summary>
    private static readonly string[] CarvedPrefixes =
    [
        "Governance_", "GovernanceCreate_", "GovernanceApplication_", "BoardVoting_",
        "ApplicationDetail_", "AdminAppDetail_", "AdminApplicationDetail_", "AdminApplications_",
        "MyApplications_"
    ];

    private static void AssertRenderedCleanly(string html, string what)
    {
        html.Should().NotContain("<vc:", $"{what} left a view-component tag unrendered");
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
        // AccessMatrixViewComponent moved into Humans.UI (nobodies-collective/Humans#1056), so
        // <vc:access-matrix section="Governance" /> binds through @addTagHelper *, Humans.UI.
        // An unbound <vc:> ships as inert literal markup with a green build and no log line, so
        // the emitted modal id is the proof — the 200 alone is not.
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
        listHtml.Should().Contain("My Applications");                 // MyApplications_Title
        listHtml.Should().NotContain("ApplicationStatus_",
            "the ApplicationStatus_* keys stayed shared and must still resolve");

        var detail = await Client.GetAsync($"/Governance/Applications/Details/{applicationId}", ct);
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        var detailHtml = await detail.Content.ReadAsStringAsync(ct);
        AssertRenderedCleanly(detailHtml, "GET /Governance/Applications/Details/{id}");
        detailHtml.Should().Contain("Application Information");       // ApplicationDetail_ApplicationInfo
        detailHtml.Should().Contain(SeededMotivation);
        // _ApplicationResponseSections and _ApplicationHistory moved into the section and are
        // now resolved by name; a partial that fails to resolve throws, one that resolves
        // against the wrong set renders raw keys.
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

        html.Should().Contain("New Application");   // GovernanceCreate_NewApplication (carved)
        // The tier radios and the Asociado question labels are Profile*/Application_* keys the
        // carve deliberately left in SharedResource; they bind SharedLocalizer in the section.
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
        listHtml.Should().Contain("Asociado Applications");   // AdminApplications_Title
        // _ApplicationsListContent moved in from Shell's Views/Shared and is now reached by
        // name across application parts rather than by ~/Views/Shared/ path.
        listHtml.Should().Contain("Applicant");               // AdminApp_Applicant, in the partial
        listHtml.Should().NotContain("AdminApp_",
            "AdminApp_Applicant stayed shared and must still resolve");

        var detail = await Client.GetAsync($"/Governance/Applications/Admin/{applicationId}", ct);
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        var detailHtml = await detail.Content.ReadAsStringAsync(ct);
        AssertRenderedCleanly(detailHtml, "GET /Governance/Applications/Admin/{id}");
        detailHtml.Should().Contain("Review Application");    // AdminApplicationDetail_Title
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
        dashboardHtml.Should().Contain("Board Voting Dashboard");   // BoardVoting_Title

        var detail = await Client.GetAsync($"/Governance/BoardVoting/{applicationId}", ct);
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        var detailHtml = await detail.Content.ReadAsStringAsync(ct);
        AssertRenderedCleanly(detailHtml, "GET /Governance/BoardVoting/{id}");
        detailHtml.Should().Contain("Votes So Far");                // BoardVoting_VotesSoFar
        // <vc:profile-card> became Component.InvokeAsync("ProfileCard", …) because the
        // component stays in Shell; the enum argument moved to Humans.UI so the section can
        // name it. An unresolvable invocation throws, so reaching this assertion is the proof.
        detailHtml.Should().Contain("Strong contributor.");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Governance_pages_render_in_spanish_from_the_sections_satellite_assemblies()
    {
        // An English-only check passes whether or not the RCL's satellites shipped — the
        // neutral set is embedded in the main assembly and the fallback is silent.
        // Razor's default HtmlEncoder escapes non-ASCII to numeric entities, so the assertions
        // stay on ASCII-only runs of the Spanish copy.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        // Accept-Language does not reach a signed-in user: Program.cs's initial culture provider
        // returns the user's PreferredLanguage and short-circuits the rest of the chain, and
        // every Governance page is [Authorize]. Switch the language the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Governance", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token!),
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
