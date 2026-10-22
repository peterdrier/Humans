using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders every Shifts page through the real app.</summary>
/// <remarks>
/// The failures this catches all render as a 200 with degraded content: a missing <c>@@using</c> or
/// <c>@@addTagHelper</c> ships literal markup, a stale <c>Url.Action</c> pair returns null and drops a link, a missed
/// asset path 404s with no <c>?v=</c> hash, and a mis-bound key renders as its own name. Spanish is a separate test
/// because a satellite assembly that never shipped still renders English.
/// </remarks>
public class ShiftsPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private const string ShiftsScriptPath = "/_content/Humans.Shifts/js/shifts.js";

    /// <summary>
    /// A mis-bound key renders as its name rather than throwing. The markup carries these prefixes only inside
    /// <c>Localizer[…]</c> and two Razor comments, so a substring check is exact.
    /// </summary>
    private static readonly string[] CarvedKeyPrefixes =
        ["Shifts_", "ShiftDash_", "ShiftInfo_", "VolTrack_", "EmailRota_", "EmailTeamRotas_"];

    /// <summary>
    /// GET routes that render without a fixture. The parameterised reads need seeded rotas and are covered by the
    /// section's controller tests.
    /// </summary>
    private static (string Url, string Copy)[] Pages =>
    [
        ("/Shifts", "Shifts"),
        ("/Shifts/Mine", "Shifts"),
        ("/Shifts/Summary", "Summary"),
        ("/Shifts/Settings", "Settings"),
        ("/Shifts/OrphanSignups", "Orphan"),
        ("/Shifts/Dashboard", "Dashboard"),
        ("/Shifts/Dashboard/PostEventStats", "Stats"),
        ("/Shifts/Admin/Workload", "Workload"),
        ("/Shifts/Dashboard/VolunteerTracking", "Volunteer"),
        ("/Shifts/ShiftInfo", "Shift"),
    ];

    /// <summary>
    /// Most pages short-circuit without an active burn, leaving nothing to assert. Seeded through the section's verb,
    /// not <c>ShiftsDbContext</c>: the section's Singleton view is warmed at startup, so a direct write is invisible to
    /// cached reads.
    /// </summary>
    private async Task SeedActiveBurnAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var eventId = Guid.NewGuid();
        await scope.ServiceProvider.GetRequiredService<IEventSettingsSeeding>().CreateActiveEventAsync(
            new EventSettingsInfo(
                Id: eventId,
                EventName: "Render Test Burn",
                Year: 2026,
                TimeZoneId: "Europe/Madrid",
                GateOpeningDate: new LocalDate(2026, 7, 1),
                BuildStartOffset: -14,
                EventEndOffset: 6,
                StrikeEndOffset: 9,
                FirstCrewStartOffset: -25,
                SetupWeekStartOffset: -16,
                PreEventWeekStartOffset: -9,
                FinishingWeekendStartOffset: -4,
                EarlyEntryCapacity: new Dictionary<int, int>(),
                BarriosEarlyEntryAllocation: null,
                EarlyEntryClose: null));
        await scope.ServiceProvider.GetRequiredService<IShiftSeeding>()
            .SetShiftBrowsingOpenAsync(eventId, isOpen: true);
    }

    [HumansFact(Timeout = 180000)]
    public async Task Every_shifts_page_renders_with_tag_helpers_applied()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedActiveBurnAsync();
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        foreach (var (url, copy) in Pages)
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);
            html.Should().Contain(copy, $"GET {url} must render its own copy");

            // ReSharper can rewrite <vc:name> to <name-view-component>, which also renders inert.
            html.Should().NotContain("-view-component", $"GET {url} has a rewritten vc tag");

            foreach (var prefix in CarvedKeyPrefixes)
                html.Should().NotContain(prefix, $"GET {url} rendered a raw {prefix}* key");
        }
    }

    /// <summary>
    /// The page also hard-codes <c>data-bs-target="#sectionHelp-Shifts"</c>, so only the <c>id=</c> the component emits
    /// proves the tag bound.
    /// </summary>
    [HumansFact(Timeout = 120000)]
    public async Task The_browse_page_renders_the_access_matrix_widget()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedActiveBurnAsync();
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Shifts", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain("id=\"sectionHelp-Shifts\"",
            "the access-matrix modal is what the page's Learn-more button targets");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Shifts_pages_render_spanish_from_the_sections_own_satellite_assemblies()
    {
        // Razor's HtmlEncoder escapes non-ASCII to entities, so every expected string below is ASCII-only.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedActiveBurnAsync();
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // Accept-Language doesn't reach a signed-in user: the PreferredLanguage provider short-circuits the chain. Switch
        // the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Shifts", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        // One page per renderer: a section view, the two actions from ProfileController, and the moved controller.
        foreach (var (url, spanish) in new[]
                 {
                     ("/Shifts/Dashboard", "Panel de turnos"),                    // ShiftDash_Title
                     ("/Shifts/ShiftInfo", "Preferencias de turnos"),         // ShiftInfo_Title
                     ("/Shifts/Dashboard/VolunteerTracking", "Seguimiento de Voluntarios"), // VolTrack_Title
                 })
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);
            html.Should().Contain(spanish, $"GET {url} must resolve its own set's es satellite");

            foreach (var prefix in CarvedKeyPrefixes)
                html.Should().NotContain(prefix, $"GET {url} rendered a raw {prefix}* key in Spanish");
        }
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

    [HumansFact(Timeout = 120000)]
    public async Task The_browse_page_links_the_sections_own_static_asset()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedActiveBurnAsync();
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var html = await (await Client.GetAsync("/Shifts", ct)).Content.ReadAsStringAsync(ct);

        // The test host composes static web assets, so a missing asset drops the ?v= hash rather than throwing.
        html.Should().Contain($"src=\"{ShiftsScriptPath}?v=",
            "the browse page must link the section's script through /_content and get a version hash");

        var asset = await Client.GetAsync(ShiftsScriptPath, ct);
        asset.StatusCode.Should().Be(HttpStatusCode.OK, "the RCL's static asset must be served");
    }

    [HumansFact(Timeout = 60000)]
    public void The_shift_info_action_pair_shell_addresses_still_resolves()
    {
        // ThingsToDo addresses the shift-info page by an (action, controller) string pair; an unresolvable pair makes
        // Url.Action return null and the to-do renders without a link on a green 200. Read from the action descriptor
        // provider, which sees what MVC really routes. Gap: this proves today's pair resolves, not that it's still used.
        var actions = Factory.Services
            .GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .Where(a => a.RouteValues.TryGetValue("controller", out var c)
                        && string.Equals(c, "ShiftProfile", StringComparison.Ordinal)
                        && a.RouteValues.TryGetValue("action", out var m)
                        && string.Equals(m, "ShiftInfo", StringComparison.Ordinal))
            .ToList();

        // Host startup is the slow part; stop here if it used the budget.
        Xunit.TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

        actions.Should().NotBeEmpty(
            "Shifts' ThingsToDo entry addresses (\"ShiftInfo\", \"ShiftProfile\") and Url.Action returns null for an unresolvable pair");

        actions.Select(a => a.AttributeRouteInfo?.Template)
            .Should().AllBe("Shifts/ShiftInfo",
                "the page takes Shifts' own URL on both halves of the split");
    }

    [HumansFact(Timeout = 60000)]
    public void No_call_site_still_addresses_shift_info_on_the_profile_controller()
    {
        // The anchor tag helper turns a stale asp-controller/asp-action pair into no href, so grep both spellings.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Humans.slnx")))
            root = root.Parent;
        root.Should().NotBeNull("the test must be able to find the repo root to scan sources");

        var stale = Directory
            .EnumerateFiles(Path.Combine(root.FullName, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadLines(f).Any(line =>
                (line.Contains("asp-action=\"ShiftInfo\"", StringComparison.Ordinal)
                 && line.Contains("asp-controller=\"Profile\"", StringComparison.Ordinal))
                || line.Contains("Url.Action(\"ShiftInfo\", \"Profile\")", StringComparison.Ordinal)))
            .Select(f => Path.GetRelativePath(root.FullName, f))
            .ToList();

        // The source scan is the slow part; stop here if it used the budget.
        Xunit.TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

        stale.Should().BeEmpty(
            "ShiftInfo lives on ShiftProfileController now; addressing it as (\"ShiftInfo\", \"Profile\") renders a link with no href instead of failing");
    }

    [HumansFact(Timeout = 120000)]
    public async Task A_volunteer_cannot_reach_the_shift_dashboard()
    {
        // Cookie auth redirects an authenticated-but-unauthorized request to AccessDeniedPath: a 302, not a 403.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        foreach (var url in new[]
                 {
                     "/Shifts/Dashboard",
                     "/Shifts/Admin/Workload",
                     "/Shifts/Dashboard/VolunteerTracking",
                     "/Shifts/Settings",
                 })
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.Found, $"GET {url} must be gated");
            response.Headers.Location?.AbsolutePath.Should().Be("/Account/AccessDenied",
                because: $"GET {url} must be denied, not sent to sign in");
        }
    }
}
