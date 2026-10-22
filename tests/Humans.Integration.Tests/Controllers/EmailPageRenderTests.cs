using System.Net;
using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Email.Data;
using Humans.Email.Domain;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders both Email pages through the real app.</summary>
/// <remarks>
/// <para>
/// A missing <c>@@using</c> or <c>@@addTagHelper</c> ships literal markup with a 200. <c>&lt;vc:human&gt;</c> on the
/// outbox grid appears only on a row with a <c>UserId</c>, which is why the fixture seeds one.
/// </para>
/// <para>
/// The <c>Email_FacilitatedMessage_*</c> keys are template copy, not page copy: the two views have no
/// <c>Localizer[…]</c> call. <c>/Email/EmailPreview</c> renders every template in all six cultures, so one GET proves
/// both the neutral set and the satellites, with no language switch.
/// </para>
/// </remarks>
public class EmailPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    /// <summary>
    /// One outbox row addressed to the signed-in admin. Without <c>UserId</c> the grid renders the raw address instead of
    /// <c>&lt;vc:human&gt;</c> and the check asserts nothing. Email has no caching decorator, so seeding through the
    /// section's own context is enough.
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

            // ReSharper can rewrite <vc:name> to <name-view-component>, which also renders inert.
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

        html.Should().Contain("Your Account Has Been Deleted");

        // A key that failed to resolve renders as its own name, in every language, silently.
        html.Should().NotContain("Email_",
            because: "an unresolved key renders as its own name, whichever section's resx it lives in");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_template_gallery_resolves_the_sections_satellite_assemblies()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var html = await (await Client.GetAsync("/Email/EmailPreview", ct))
            .Content.ReadAsStringAsync(ct);

        // The gallery renders every culture, so Spanish needs no culture switch. HtmlEncoder escapes non-ASCII, so the
        // check stays on an accent-free run.
        html.Should().Contain("cuenta ha sido eliminada",
            because: "an RCL whose satellite assemblies do not reach the host's probing path "
                + "falls back to the neutral set in silence");
    }
}
