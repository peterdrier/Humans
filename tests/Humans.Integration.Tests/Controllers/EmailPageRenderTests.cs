using System.Net;
using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Email.Data;
using Humans.Email.Domain;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders both Email pages through the real app, as the standing form of the §15 step 12
/// check for the section's move into <c>src/Sections/Humans.Email</c>
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// <para>
/// The failure mode a G5 move introduces renders as a <b>200 with degraded content</b>: a
/// section RCL does not inherit the host's <c>Views/_ViewImports.cshtml</c>, so a missing
/// <c>@@using</c> or <c>@@addTagHelper</c> ships literal markup with a green build, and an
/// unrendered <c>&lt;vc:…&gt;</c> element is inert text the browser simply drops.
/// <c>&lt;vc:human&gt;</c> on the outbox grid is the one Email uses — and it only appears on a
/// row that carries a <c>UserId</c>, which is why the fixture seeds one.
/// </para>
/// <para>
/// The resx half is unusual for a section: the 70 <c>Email_*</c> keys are not this section's
/// admin-page copy — those two views carry no <c>Localizer[…]</c> call at all — they are every
/// section's transactional email text, and they came here with <c>EmailRenderer</c>, their one
/// and only renderer. <c>/Email/EmailPreview</c> is therefore the ideal probe: it renders 20
/// templates in all six cultures on one page, so a single GET proves both that the neutral set
/// resolves and that the RCL's satellite assemblies reach the host's probing path — no
/// language-switcher round trip needed (contrast CityPlanning, whose Spanish check has to POST
/// <c>/Language/SetLanguage</c> first).
/// </para>
/// </remarks>
public class EmailPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    /// <summary>
    /// One outbox row addressed to the signed-in admin. Without <c>UserId</c> the grid renders
    /// the raw address instead of <c>&lt;vc:human&gt;</c> and the check asserts nothing. Each
    /// integration test class owns its database (nobodies-collective/Humans#983), so this
    /// disturbs nothing. Email has no §15 caching decorator, so seeding through the section's
    /// own context is enough.
    /// </summary>
    private async Task SeedOutboxMessageAsync(Guid adminUserId, CancellationToken ct)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();

        db.EmailOutboxMessages.Add(new EmailOutboxMessage
        {
            Id = Guid.NewGuid(),
            RecipientEmail = "render-test@example.com",
            RecipientName = "Render Test",
            Subject = "Seeded by EmailPageRenderTests",
            HtmlBody = "<p>Seeded</p>",
            TemplateName = "render_test",
            UserId = adminUserId,
            Status = EmailOutboxStatus.Sent,
            CreatedAt = now,
            SentAt = now,
        });

        await db.SaveChangesAsync(ct);
    }

    [HumansFact(Timeout = 120000)]
    public async Task Every_email_page_renders_without_unbound_tag_helpers()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var adminUserId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        await SeedOutboxMessageAsync(adminUserId, ct);

        string[] pages = ["/Email/EmailOutbox", "/Email/EmailPreview"];

        foreach (var url in pages)
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);

            // A view component element the section's _ViewImports failed to bind renders as
            // literal <vc:…> markup: 200, correct-looking source, nothing on the page.
            html.Should().NotContain("<vc:", $"GET {url} left a view-component tag unrendered");

            // ReSharper's rename refactoring rewrites <vc:name> to <name-view-component>,
            // which is equally inert (the "<vc:*> rename hazard" in G5-SECTION-TEMPLATE.md).
            html.Should().NotContain("-view-component", $"GET {url} left a renamed view-component tag unrendered");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_outbox_grid_renders_its_recipient_through_the_human_component()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var adminUserId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        await SeedOutboxMessageAsync(adminUserId, ct);

        var html = await (await Client.GetAsync("/Email/EmailOutbox", ct))
            .Content.ReadAsStringAsync(ct);

        html.Should().Contain("render-test@example.com");
        html.Should().Contain("render_test", because: "the grid's Template column names the seeded row");
        html.Should().Contain($"data-user-id=\"{adminUserId}\"",
            because: "the rendered <vc:human> emits the popover hook; the unrendered literal element does not");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_template_gallery_resolves_the_sections_own_resource_set()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var html = await (await Client.GetAsync("/Email/EmailPreview", ct))
            .Content.ReadAsStringAsync(ct);

        // The neutral set, embedded in Humans.Email itself.
        html.Should().Contain("Your Account Has Been Deleted");

        // A key that failed to resolve renders as its own name, in every language, silently.
        html.Should().NotContain("Email_",
            because: "every Email_* key moved into EmailResource with EmailRenderer");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_template_gallery_resolves_the_sections_satellite_assemblies()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var html = await (await Client.GetAsync("/Email/EmailPreview", ct))
            .Content.ReadAsStringAsync(ct);

        // The gallery renders every template per culture, so the Spanish satellite is proved
        // without switching the request culture. Razor escapes non-ASCII to numeric entities
        // ("cuenta ha sido eliminada" is the accent-free run of "Su cuenta ha sido eliminada").
        html.Should().Contain("cuenta ha sido eliminada",
            because: "an RCL whose satellite assemblies do not reach the host's probing path "
                + "falls back to the neutral set in silence");
    }
}
