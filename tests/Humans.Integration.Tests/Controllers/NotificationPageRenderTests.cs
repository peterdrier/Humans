using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Humans.Notifications.Contracts;
using Humans.Notifications.Data;
using Humans.Notifications.Domain;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders every Notifications surface through the real app, as the standing form of the
/// §15 step 12 check for the section's move into <c>src/Sections/Humans.Notifications</c>
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
/// <c>@@using</c> or <c>@@addTagHelper</c> ships literal markup with a green build.
/// </description></item>
/// <item><description>
/// The resx carve moved all 38 <c>Notification_*</c> keys out of <c>SharedResource</c>. A key
/// the carve missed, or a call site bound to the wrong set, renders the raw key — in every
/// language, with no error. The Spanish request is the only thing that proves the section
/// RCL's satellite assemblies reach the host's probing path.
/// </description></item>
/// <item><description>
/// Two of those keys are on the notification bell, which is section-owned but rendered from
/// Shell's <c>_Layout</c> and <c>_AdminLayout</c>. The component is
/// <c>internal</c>, so MVC only finds it through
/// <c>Humans.Web/Infrastructure/SectionViewComponentFeatureProvider</c>; without that,
/// <c>Component.InvokeAsync("NotificationBell")</c> throws on every authenticated page. That
/// one is loud rather than silent — but the bell resolving against the <i>wrong</i> resource
/// set is not, which is what the aria-label assertions below cover.
/// </description></item>
/// </list>
/// <para>
/// Notifications ships no <c>wwwroot/</c>, so there is no static-asset half here.
/// </para>
/// </remarks>
public class NotificationPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private const string ActionableTitle = "Two shifts still need cover on Thursday";
    private const string InformationalTitle = "You were added to the Geeks team";

    /// <summary>
    /// Writes one actionable and one informational notification straight through the section's
    /// own DbContext. There is no §15 caching decorator in front of the inbox service — it
    /// caches only the per-user badge count — so the context write is what the inbox and popup
    /// reads see. Without rows, <c>/Notifications</c> renders its empty state and most of the
    /// carved keys never render. Each integration test class owns its database
    /// (nobodies-collective/Humans#983).
    /// </summary>
    private async Task SeedNotificationsAsync(Guid userId, CancellationToken ct)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        foreach (var (title, cls, source) in new[]
                 {
                     (ActionableTitle, NotificationClass.Actionable, NotificationSource.ShiftCoverageGap),
                     (InformationalTitle, NotificationClass.Informational, NotificationSource.TeamMemberAdded),
                 })
        {
            var id = Guid.NewGuid();
            var notification = new Notification
            {
                Id = id,
                Title = title,
                Priority = NotificationPriority.Normal,
                Source = source,
                Class = cls,
                CreatedAt = now,
            };
            notification.Recipients.Add(new NotificationRecipient { NotificationId = id, UserId = userId });
            db.Notifications.Add(notification);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<Guid> SignInAndSeedAsync(CancellationToken ct)
    {
        var userId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        await SeedNotificationsAsync(userId, ct);
        return userId;
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_inbox_renders_without_raw_resource_keys_or_unbound_tag_helpers()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SignInAndSeedAsync(ct);

        var response = await Client.GetAsync("/Notifications?tab=all", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);

        // A view component element the section's _ViewImports failed to bind renders as
        // literal <vc:…> markup: 200, correct-looking source, nothing on the page.
        html.Should().NotContain("<vc:", "GET /Notifications left a view-component tag unrendered");

        // The fallback for a key the carve missed is the key itself.
        html.Should().NotContain("Notification_", "GET /Notifications rendered a raw resource key");

        html.Should().Contain("All notifications");        // Notification_PageTitle
        html.Should().Contain("Needs your attention");     // Notification_Section_NeedsAttention
        html.Should().Contain("Search notifications...");  // Notification_SearchPlaceholder
        html.Should().Contain(ActionableTitle);
        html.Should().Contain(InformationalTitle);
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_bell_popup_partial_renders_its_own_copy()
    {
        // /Notifications/Popup is the partial the bell dropdown fetches. It has its own
        // localizer bindings and its own row partial, so a _ViewImports gap shows up here
        // and nowhere else.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SignInAndSeedAsync(ct);

        var response = await Client.GetAsync("/Notifications/Popup", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().NotContain("<vc:");
        html.Should().NotContain("Notification_", "the popup partial rendered a raw resource key");
        html.Should().Contain("Notifications");   // Notification_Popup_Title
        html.Should().Contain(ActionableTitle);
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_bell_is_discovered_and_rendered_from_shells_layout()
    {
        // The bell view component is internal to the section. Without
        // SectionViewComponentFeatureProvider, Component.InvokeAsync("NotificationBell") in
        // Shell's _Layout throws and this page 500s — so the status code is a real assertion
        // here. /Teams is a Shell page, so nothing about the section's own routing is in the
        // way. Its aria-label proves it resolved the section's resource set, not Shell's.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SignInAndSeedAsync(ct);

        var response = await Client.GetAsync("/Teams", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("id=\"notificationBellWrapper\"", "the bell component did not render");
        html.Should().Contain("aria-label=\"Notifications\"");
        html.Should().NotContain("Notification_Bell_AriaLabel");
        html.Should().NotContain("Notification_Popup_AriaLabel");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Notification_pages_render_in_spanish_from_the_sections_satellite_assemblies()
    {
        // An English-only check passes whether or not the RCL's satellites shipped — the neutral
        // set is embedded in the main assembly and the fallback is silent.
        // Razor's default HtmlEncoder escapes non-ASCII to numeric entities, so the assertions
        // stay on ASCII-only runs of the Spanish copy.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SignInAndSeedAsync(ct);

        // Accept-Language does not reach a signed-in user: Program.cs's initial culture provider
        // returns the user's PreferredLanguage and short-circuits the rest of the chain, and
        // /Notifications is [Authorize]. Switch the language the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Notifications", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token!),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var response = await Client.GetAsync("/Notifications?tab=all", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("Todas las notificaciones");   // Notification_PageTitle
        html.Should().Contain("Buscar notificaciones");      // Notification_SearchPlaceholder
        html.Should().Contain("Requiere tu atenci");         // Notification_Section_NeedsAttention
        html.Should().NotContain("All notifications");
        html.Should().NotContain("Notification_");
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
