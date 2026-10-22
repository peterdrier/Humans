using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders every MailerLite page through the real app.</summary>
/// <remarks>
/// <para>
/// No resource set or <c>wwwroot/</c>. An unbound <c>&lt;vc:human&gt;</c> survives as inert markup with a 200, so the
/// import page is seeded with a subscriber matching the signed-in admin to give it something to render.
/// </para>
/// <para>
/// The controller names its views by absolute path, resolved against the section's compiled views, and
/// <c>_ViewStart</c>'s <c>_AdminLayout</c> resolves into <c>Humans.Web</c> across application parts. Both fail at
/// request time, not build.
/// </para>
/// <para>
/// MailerLite itself is <see cref="StubMailerLiteService"/>: a render test must not depend on the remote.
/// </para>
/// </remarks>
public class MailerLitePageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 120000)]
    public async Task Every_mailer_page_renders_its_own_copy_with_tag_helpers_applied()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // The admin persona's verified address, so the preview renders a <vc:human> for the match.
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

            html.Should().NotContain("-view-component", $"GET {url} has a rewritten <vc:> element");

            // _AdminLayout resolves across application parts and a miss throws, so the admin chrome is the proof.
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
    /// Every <c>/MailerLite/Admin/*</c> route is <c>AdminOnly</c>, on an internal controller routed through
    /// <c>SectionControllerFeatureProvider</c>.
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
