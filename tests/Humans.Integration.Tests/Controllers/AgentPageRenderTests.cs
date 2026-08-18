using System.Net;
using AwesomeAssertions;
using Humans.Agent.Data;
using Humans.Agent.Domain;
using Humans.Agent.Services;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders every Agent page through the real app, as the standing form of the §15 step 12
/// check for the section's move into <c>src/Sections/Humans.Agent</c>
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// <para>
/// Written and run <em>before</em> the move so it is a genuine baseline, then re-run after each
/// risky step. The three failure modes it exists to catch all render as a <b>200 with degraded
/// content</b>, which is why "the page loads" is not the assertion:
/// </para>
/// <list type="number">
/// <item><description>
/// A section RCL does not inherit the host's <c>Views/_ViewImports.cshtml</c> — a missing
/// <c>@@using</c> or <c>@@addTagHelper</c> ships literal markup with a green build.
/// </description></item>
/// <item><description>
/// A key that did not make it into the carved <c>AgentResource</c> set falls back to rendering
/// its own raw name, in every language, with no error.
/// </description></item>
/// <item><description>
/// A static asset that moved to the section's RCL is served from the static-web-assets manifest
/// rather than from <c>wwwroot</c> on disk, so <c>asp-append-version</c> can silently emit no
/// <c>?v=</c> hash while the page still returns 200 (§15 step 7, the first section to move any).
/// </description></item>
/// </list>
/// </remarks>
public class AgentPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    /// <summary>
    /// Where the agent's stylesheet is served from. Shell's <c>HelpWidget</c> and the section's
    /// own <c>Conversation.cshtml</c> both link it, so it pins step 7 from both sides.
    /// </summary>
    private const string AgentCssPath = "/_content/Humans.Agent/css/agent.css";

    private const string AgentWidgetJsPath = "/_content/Humans.Agent/js/agent/widget.js";

    [HumansFact(Timeout = 60000)]
    public async Task My_conversations_renders_localized_copy_from_the_sections_own_resource_set()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var response = await Client.GetAsync("/Agent/Conversations", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("My Conversations");      // Agent_HistoryTitle
        html.Should().Contain("No conversations yet.");  // Agent_HistoryEmpty
        html.Should().NotContain("Agent_",
            because: "an unresolved key renders as its own literal name and survives an "
                   + "English-only eyeball check of the page");
    }

    [HumansFact(Timeout = 60000)]
    public async Task My_conversations_renders_in_Spanish_from_the_sections_satellite_assembly()
    {
        // The check an English-only capture cannot make: the neutral set is embedded in the main
        // assembly, so English passes whether or not the section's satellite assemblies shipped.
        //
        // Accept-Language alone would not do it: every Agent page is behind [Authorize], and the
        // app's initial culture provider answers from the signed-in user's PreferredLanguage
        // before the header provider ever runs (Program.cs). Setting it through IUserService is
        // also what invalidates the cached UserInfo that provider reads.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);
        await SetPreferredLanguageAsync(userId, "es", ct);

        var response = await Client.GetAsync("/Agent/Conversations", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        // Razor's HtmlEncoder escapes non-ASCII to numeric entities, so the assertions stay on
        // accent-free runs of the Spanish copy.
        html.Should().Contain("Mis conversaciones");        // Agent_HistoryTitle
        html.Should().Contain("n no tienes conversaciones"); // Agent_HistoryEmpty ("Aún no…")
        html.Should().NotContain("No conversations yet.");
    }

    [HumansFact(Timeout = 60000)]
    public async Task Conversation_transcript_renders_every_carved_key()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);
        var conversationId = await SeedConversationAsync(userId, ct);

        var response = await Client.GetAsync($"/Agent/Conversation/{conversationId}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("Agent conversation");                      // Agent_ConversationTitle
        html.Should().Contain("Transcript");                              // Agent_ConversationTranscript
        html.Should().Contain("Your context (as the agent sees you now)"); // Agent_ContextCardSummary
        html.Should().Contain("Back to history");                         // Agent_ConversationBackToHistory
        html.Should().Contain("hello from the render test");
        html.Should().NotContain("Agent_");
    }

    [HumansFact(Timeout = 60000)]
    public async Task Conversation_page_links_the_sections_stylesheet_with_a_cache_busting_hash()
    {
        // §15 step 7: the section's own view links the asset. asp-append-version resolves the file
        // through the host's IFileProvider; once the file lives in an RCL it is served from the
        // static-web-assets manifest instead of wwwroot on disk, and a provider that cannot see it
        // emits the bare href with no ?v= rather than failing.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);
        var conversationId = await SeedConversationAsync(userId, ct);

        var response = await Client.GetAsync($"/Agent/Conversation/{conversationId}", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain($"href=\"{AgentCssPath}?v=",
            because: "asp-append-version must still resolve the stylesheet after it moves");
    }

    [HumansFact(Timeout = 60000)]
    public async Task Help_widget_links_the_agent_assets_with_cache_busting_hashes()
    {
        // The other two reference sites for the section's static assets, both in Shell's own
        // chrome: HelpWidget links the stylesheet always and the script only when the agent is on.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);
        await EnableAgentAsync(ct);

        var response = await Client.GetAsync("/Agent/Conversations", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("Assistant");                       // Agent_PanelTitle — widget is on
        html.Should().Contain($"href=\"{AgentCssPath}?v=");
        html.Should().Contain($"src=\"{AgentWidgetJsPath}?v=");
    }

    [HumansFact(Timeout = 60000)]
    public async Task The_sections_static_assets_are_served_from_its_own_RCL()
    {
        // The other half of §15 step 7: the href resolving is not the same as the file being
        // there. Before the test host composed the static-web-assets manifest both agent assets
        // 404'd here while the pages still returned 200 — the exact shape the step warns about.
        var ct = Xunit.TestContext.Current.CancellationToken;

        foreach (var asset in new[] { AgentCssPath, AgentWidgetJsPath })
        {
            var response = await Client.GetAsync(asset, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {asset} must be served");
        }
    }

    [HumansFact(Timeout = 60000)]
    public async Task Admin_agent_pages_render()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        var conversationId = await SeedConversationAsync(userId, ct);

        var urls = new[]
        {
            "/Agent/Admin/Status",
            "/Agent/Admin/Settings",
            "/Agent/Conversations",
            $"/Agent/Conversations/{conversationId}",
        };

        // /Agent/Admin/Conversations/{id}/Prompt is deliberately absent: it rebuilds the preload
        // corpus, which fetches every section guide from GitHub over the network. Including it
        // made this test fail on network weather rather than on rendering.

        foreach (var url in urls)
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);
            html.Should().NotContain("Agent_", $"GET {url} rendered a raw resource key");
        }
    }

    private async Task SetPreferredLanguageAsync(Guid userId, string culture, CancellationToken ct)
    {
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserService>();
        await users.SetPreferredLanguageAsync(userId, culture, ct);
    }

    private async Task EnableAgentAsync(CancellationToken ct)
    {
        using var scope = Factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAgentSettingsService>();
        await settings.UpdateAsync(s => s.Enabled = true, ct);
    }

    private async Task<Guid> SeedConversationAsync(Guid userId, CancellationToken ct)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

        var startedAt = Instant.FromUtc(2026, 5, 1, 12, 0);
        var conversation = new AgentConversation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            StartedAt = startedAt,
            LastMessageAt = startedAt,
            Locale = "en",
            MessageCount = 1,
        };
        db.AgentConversations.Add(conversation);
        db.AgentMessages.Add(new AgentMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            Role = AgentRole.User,
            Content = "hello from the render test",
            CreatedAt = startedAt,
            Model = "claude-sonnet-4-6",
        });
        await db.SaveChangesAsync(ct);
        return conversation.Id;
    }
}
