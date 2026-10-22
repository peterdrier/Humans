using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Humans.Notifications.Contracts;
using Humans.Notifications.Data;
using Humans.Notifications.Domain;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders every Notifications surface through the real app.</summary>
/// <remarks>
/// Failures render as a 200 with degraded content: a mis-bound key renders as its name, and only Spanish proves the
/// satellites ship. The bell is internal and rendered from Shell's layouts: a discovery miss throws loudly, but the
/// bell binding the wrong resource set does not, which the aria-label checks cover.
/// </remarks>
public class NotificationPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private const string ActionableTitle = "Two shifts still need cover on Thursday";
    private const string InformationalTitle = "You were added to the Geeks team";

    /// <summary>
    /// Written straight to the context: no caching decorator fronts the inbox service. Without rows the inbox renders
    /// its empty state and most keys never show.
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

        html.Should().NotContain("Notifications_", "GET /Notifications rendered a raw resource key");

        html.Should().Contain("All notifications");        // Notifications_PageTitle
        html.Should().Contain("Needs your attention");     // Notifications_Section_NeedsAttention
        html.Should().Contain("Search notifications...");  // Notifications_SearchPlaceholder
        html.Should().Contain(ActionableTitle);
        html.Should().Contain(InformationalTitle);
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_bell_popup_partial_renders_its_own_copy()
    {
        // The bell dropdown's partial has its own localizer bindings and row partial, so a _ViewImports gap shows here
        // only.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SignInAndSeedAsync(ct);

        var response = await Client.GetAsync("/Notifications/Popup", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().NotContain("Notifications_", "the popup partial rendered a raw resource key");
        html.Should().Contain("Notifications");   // Notifications_Popup_Title
        html.Should().Contain(ActionableTitle);
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_bell_is_discovered_and_rendered_from_shells_layout()
    {
        // Without SectionViewComponentFeatureProvider the bell throws in _Layout and this Shell page 500s. The
        // aria-label proves it resolved the section's resource set.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SignInAndSeedAsync(ct);

        var response = await Client.GetAsync("/Teams", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("id=\"notificationBellWrapper\"", "the bell component did not render");
        html.Should().Contain("aria-label=\"Notifications\"");
        html.Should().NotContain("Notifications_Bell_AriaLabel");
        html.Should().NotContain("Notifications_Popup_AriaLabel");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Notification_pages_render_in_spanish_from_the_sections_satellite_assemblies()
    {
        // The neutral set is embedded in the main assembly and fallback is silent, so only Spanish proves the
        // satellites. HtmlEncoder escapes non-ASCII, so the checks stay on ASCII runs.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SignInAndSeedAsync(ct);

        // Accept-Language doesn't reach a signed-in user: the PreferredLanguage provider short-circuits the chain. Switch
        // the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Notifications", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var response = await Client.GetAsync("/Notifications?tab=all", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("Todas las notificaciones");   // Notifications_PageTitle
        html.Should().Contain("Buscar notificaciones");      // Notifications_SearchPlaceholder
        html.Should().Contain("Requiere tu atenci");         // Notifications_Section_NeedsAttention
        html.Should().NotContain("All notifications");
        html.Should().NotContain("Notifications_");
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
