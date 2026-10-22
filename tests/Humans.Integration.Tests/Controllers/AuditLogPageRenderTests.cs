using System.Net;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders every Audit Log page through the real app.</summary>
/// <remarks>
/// AuditLog ships no resource set, so instead of a raw-key probe the check is the <c>&lt;page-header&gt;</c> tag helper,
/// which survives as its own start tag when unbound. One authorized and one unauthorized GET prove the section's
/// internal controller meets Shell's policy.
/// </remarks>
public class AuditLogPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private static string[] Pages =>
    [
        "/AuditLog",
    ];

    [HumansFact(Timeout = 120000)]
    public async Task Every_audit_log_page_renders_with_tag_helpers_applied()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        foreach (var url in Pages)
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);
            html.Should().Contain("Audit Log", $"GET {url} must render its own copy");

            html.Should().NotContain("<page-header", $"GET {url} left <page-header> unbound");

            // ReSharper can rewrite <vc:name> to <name-view-component>, which also renders inert.
            html.Should().NotContain("-view-component", $"GET {url} has a rewritten vc tag");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task Audit_log_is_closed_to_a_non_privileged_human()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var response = await Client.GetAsync("/AuditLog", ct);

        // Cookie auth redirects an authenticated-but-unauthorized request to AccessDeniedPath: a 302, not a 403.
        response.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "the BoardOrAdmin policy in Shell must still gate the section's controller");
    }

    /// <summary>
    /// Every assembly rendering <c>&lt;vc:audit-log&gt;</c> needs its own <c>@addTagHelper *, Humans.AuditLog</c>. A
    /// seeded marker reaching the HTML proves the tag bound and the component and its views resolved; an absent
    /// <c>&lt;vc:</c> proves nothing.
    /// </summary>
    [HumansFact(Timeout = 120000)]
    public async Task The_audit_log_view_component_binds_and_renders_in_every_consuming_assembly()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var adminId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // ShiftSignupBailed renders its Description as a tail, which carries the marker into the HTML.
        var marker = $"vc-binding-probe-{Guid.NewGuid():N}";
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var auditLog = scope.ServiceProvider.GetRequiredService<IAuditLogService>();
            await auditLog.LogAsync(
                AuditAction.ShiftSignupBailed,
                entityType: "User",
                entityId: adminId,
                description: marker,
                actorUserId: adminId);
        }

        // Both render the widget for the signed-in user with no extra fixtures.
        foreach (var url in new[] { $"/Users/Admin/{adminId}", "/Debug/WidgetGallery" })
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);
            html.Should().Contain(marker,
                $"GET {url}: the seeded audit row must reach the page — an unbound <vc:audit-log> renders nothing");
            html.Should().NotContain("audit-log-view-component",
                $"GET {url}: a ReSharper-rewritten vc tag is inert markup too");
        }
    }

    /// <summary>
    /// <c>Views/Profile</c> has a different <c>_ViewImports.cshtml</c> from <c>Views/UsersAdmin</c>. The panel is
    /// coordinator-gated and never shows on one's own profile, so this signs in twice.
    /// </summary>
    [HumansFact(Timeout = 120000)]
    public async Task The_profile_sent_messages_panel_renders_through_the_table_layout()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var volunteerId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);
        var adminId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var marker = $"sent-message-probe-{Guid.NewGuid():N}";
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var auditLog = scope.ServiceProvider.GetRequiredService<IAuditLogService>();
            await auditLog.LogAsync(
                AuditAction.FacilitatedMessageSent,
                entityType: "User",
                entityId: volunteerId,
                description: marker,
                actorUserId: adminId);
        }

        var url = $"/Profile/{volunteerId}";
        var response = await Client.GetAsync(url, ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain(marker,
            $"GET {url}: the seeded audit row must reach the panel — an unbound <vc:audit-log> renders nothing");
    }

    /// <summary>
    /// AuditLog ships no resource set, so the host passes localized headers through <c>column-labels</c>; otherwise a
    /// translated member page gets three English headers.
    /// </summary>
    [HumansFact(Timeout = 120000)]
    public async Task The_profile_sent_messages_panel_localizes_its_column_headers()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var volunteerId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);
        var adminId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // Headers render only when the table has rows.
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var auditLog = scope.ServiceProvider.GetRequiredService<IAuditLogService>();
            await auditLog.LogAsync(
                AuditAction.FacilitatedMessageSent,
                entityType: "User",
                entityId: volunteerId,
                description: $"header-localization-probe-{Guid.NewGuid():N}",
                actorUserId: adminId);
        }

        var url = $"/Profile/{volunteerId}";

        // Accept-Language doesn't reach a signed-in user: the PreferredLanguage provider short-circuits the chain. Switch
        // the way the UI does.
        var token = ExtractAntiForgeryToken(await (await Client.GetAsync(url, ct)).Content.ReadAsStringAsync(ct));
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var response = await Client.GetAsync(url, ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain("Mensajes enviados", "Profile_SentMessages is the panel title");
        html.Should().Contain("Remitente", "Common_Sender must reach the Actor column header");
        html.Should().Contain("Vista previa", "Common_Preview must reach the Description column header");
        html.Should().NotContain("Sender", "the English default header must not survive column-labels");
        html.Should().NotContain("Preview", "the English default header must not survive column-labels");
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

    /// <summary>
    /// The call sites that need seeded data to render, covered structurally, along with any added later.
    /// </summary>
    [HumansFact]
    public void Every_audit_log_call_site_sits_under_a_view_imports_that_binds_it()
    {
        const string Directive = "@addTagHelper *, Humans.AuditLog";
        var src = Path.Combine(FindRepoRoot(), "src");

        var callSites = Directory
            .EnumerateFiles(src, "*.cshtml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("<vc:audit-log", StringComparison.Ordinal))
            .ToList();

        callSites.Should().NotBeEmpty("the component would otherwise be dead");

        var unbound = callSites.Where(f => !BindsAuditLog(f, src, Directive)).ToList();

        unbound.Should().BeEmpty(
            "a <vc:audit-log> whose assembly never opens Humans.AuditLog's tag helpers ships as "
            + "inert literal markup with a green build and no runtime error");
    }

    // Razor applies every _ViewImports.cshtml from the project root down.
    private static bool BindsAuditLog(string viewPath, string srcRoot, string directive)
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(viewPath)!);
             dir is not null && dir.FullName.StartsWith(srcRoot, StringComparison.Ordinal);
             dir = dir.Parent)
        {
            var imports = Path.Combine(dir.FullName, "_ViewImports.cshtml");
            if (File.Exists(imports)
                && File.ReadAllText(imports).Contains(directive, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Humans.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
