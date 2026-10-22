using System.Net;
using AwesomeAssertions;
using Humans.Agent.Data;
using Humans.Agent.Domain;
using Humans.Agent.Services;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.Users.Contracts;
using Humans.Agent.Contracts;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders every Agent page through the real app.</summary>
/// <remarks>
/// Failures render as a 200 with degraded content: unbound tags ship as markup, a mis-bound key renders as its name,
/// and an RCL asset can lose its <c>?v=</c> hash silently.
/// </remarks>
public class AgentPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    /// <summary>Linked by Shell's <c>HelpWidget</c> and the section's <c>Conversation.cshtml</c> both.</summary>
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
        // Only Spanish proves the satellites shipped. Accept-Language doesn't reach a signed-in user, so set the
        // preference through IUserService, which also invalidates the cached UserInfo the culture provider reads.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);
        await SetPreferredLanguageAsync(userId, "es", ct);

        var response = await Client.GetAsync("/Agent/Conversations", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        // HtmlEncoder escapes non-ASCII, so the checks stay on ASCII runs.
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
        // An RCL asset comes from the static-web-assets manifest; a file provider that can't see it emits the bare href
        // with no ?v= rather than failing.
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
        // HelpWidget links the stylesheet always and the script only when the agent is on.
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
        // An href resolving doesn't mean the file is served.
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
            "/Agent/Conversations",
            $"/Agent/Conversations/{conversationId}",
        };

        // /Agent/Admin/Settings has no GET (the form lives at /Settings#agent). The Prompt page is left out: it fetches
        // every section guide from GitHub, so it would fail on network weather.

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
        var users = scope.ServiceProvider.GetRequiredService<IUserWrite>();
        await users.SetPreferredLanguageAsync(userId, culture, ct);
    }

    private async Task EnableAgentAsync(CancellationToken ct)
    {
        using var adminClient = Factory.CreateClient();
        var actorUserId = await Factory.SignInAsFullyOnboardedAsync(adminClient, DevPersona.Admin);
        using var scope = Factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAgentSettingsService>();
        await settings.UpdateAsync(s => s.Enabled = true, actorUserId, ct);
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
