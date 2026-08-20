using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders every MailerLite page through the real app, as the standing form of the §15 step 12
/// check for the section's move into <c>src/Sections/Humans.MailerLite</c>
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// <para>
/// MailerLite has none of the usual halves — no resource set (the two admin pages are English
/// operator copy, so there is no <c>NotContain("MailerLite_")</c> and no Spanish request) and no
/// <c>wwwroot/</c>, so no <c>?v=</c>. What a G5 move actually breaks here is the section
/// RCL's own <c>Views/_ViewImports.cshtml</c>, which the host's does not supply: an unbound
/// <c>&lt;vc:human&gt;</c> survives into the response as inert literal markup with a green
/// build, a 200 and correct-looking source, so the assertions are on the *rendered* form.
/// The import page is seeded with one subscriber matching the signed-in admin precisely so
/// that a <c>&lt;vc:human&gt;</c> has something to render.
/// </para>
/// <para>
/// The second thing the move touches is view lookup: the controller names its views by
/// absolute path (<c>~/Views/MailerLite/Admin/Index.cshtml</c>), which now resolves against the
/// section assembly's compiled views rather than Shell's, and <c>_ViewStart</c>'s
/// <c>Layout = "_AdminLayout"</c> resolves back into <c>Humans.Web</c> across application
/// parts (it was <c>Humans.UI</c> until G5 lane 4b-ii). Both fail at request time rather
/// than at build.
/// </para>
/// <para>
/// MailerLite itself is <see cref="StubMailerLiteService"/> — the pages are a diff against a
/// remote the section does not own, and a render test must not depend on it.
/// </para>
/// </remarks>
public class MailerLitePageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 120000)]
    public async Task Every_mailer_page_renders_its_own_copy_with_tag_helpers_applied()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // The admin persona's verified address, so the import plan classifies it as a
        // verified match and the preview renders a <vc:human> for the matched human.
        Factory.MailerLiteServiceStub.AddWebsiteSubscriber("dev-admin@localhost");

        (string Url, string Copy)[] pages =
        [
            ("/MailerLite/Admin", "MailerLite Dashboard"),
            ("/MailerLite/Admin/Import", "Import Preview"),
            ("/MailerLite/Admin/Audiences/ticket-no-shifts/Debug", "Audience Debug"),
        ];

        foreach (var (url, copy) in pages)
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);
            html.Should().Contain(copy, $"GET {url} must render its own copy");

            // A section can only addTagHelper against Humans.UI; a component element the
            // section's _ViewImports failed to bind survives into the body as its own
            // element name — 200, correct-looking source, nothing on screen.
            html.Should().NotContain("<vc:", $"GET {url} left a view-component tag unrendered");
            html.Should().NotContain("-view-component", $"GET {url} has a rewritten <vc:> element");

            // _ViewStart's Layout = "_AdminLayout" resolves into Humans.Web across
            // application parts; a miss throws, so reaching the admin chrome is the proof.
            html.Should().Contain("admin-shell", $"GET {url} did not render inside _AdminLayout");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task Import_preview_renders_the_matched_human_through_the_view_component()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var adminId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        Factory.MailerLiteServiceStub.AddWebsiteSubscriber("dev-admin@localhost");

        var response = await Client.GetAsync("/MailerLite/Admin/Import", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain("dev-admin@localhost", "the Website subscriber must appear in the plan");
        html.Should().Contain(adminId.ToString(),
            "<vc:human> renders the matched human's admin link, which carries the user id — " +
            "an unbound tag helper would leave the element inert instead");
    }

    /// <summary>
    /// Every <c>/MailerLite/Admin/*</c> route is <c>AdminOnly</c>. The controller is now
    /// <c>internal</c> and routed through <c>SectionControllerFeatureProvider</c>, so the
    /// negative access rule is worth re-pinning at the move.
    /// </summary>
    [HumansFact(Timeout = 120000)]
    public async Task Non_admin_cannot_reach_the_mailer_dashboard()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var response = await Client.GetAsync("/MailerLite/Admin", ct);
        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }
}
