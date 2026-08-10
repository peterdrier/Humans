using Humans.Agent.Authorization;
using Humans.Agent.Contracts;
using Humans.Agent.Data;
using Humans.Agent.Filters;
using Humans.Agent.Services;
using Humans.Agent.Services.Anthropic;
using Humans.Agent.Services.Preload;
using Humans.Agent.Services.Stores;
using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Hosting;
using Humans.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Agent;

/// <summary>
/// Agent's DI entry point, at the project root by convention. Discovered by Shell — nothing
/// names it, so it needs no section prefix. Replaces Shell's <c>AddAgentSection</c> verbatim
/// (design §6): same lifetimes, same order, same options bindings.
/// </summary>
/// <remarks>
/// <para>
/// <c>AgentConversationRetentionJob</c> is <em>not</em> registered here: recurring jobs are
/// named by concrete type in Shell's <c>UseHumansRecurringJobs</c> roll-call and there is no
/// discovery seam for them yet, so it stays in <c>Humans.Infrastructure/Jobs</c> and reaches
/// the section through <see cref="IAgentConversationRetention"/> (design §15.6b).
/// </para>
/// <para>
/// The two warm-up hosted services <em>are</em> registered here. Nothing outside the section
/// names them — <c>AddHostedService&lt;T&gt;</c> resolves T at the registration site — so
/// moving them in is what keeps <c>IAgentPreloadCorpusBuilder</c> and
/// <c>IAgentSettingsService</c> off the contracts leaf.
/// </para>
/// <para>
/// <c>GitHubCommunityKbContentSource</c> stays in <c>Humans.Infrastructure</c>: it is a
/// GitHub content connector implementing a Base interface. The <em>Anthropic</em> connector
/// did not get to stay — <c>IAnthropicClient</c> streams the section's own
/// <c>AgentTurnToken</c>, so leaving it in Base would have meant promoting that type
/// downward, which is the one thing §15.5b forbids.
/// </para>
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<AgentDbContext>(sentinelTable: "agent_conversations");

        services.Configure<AnthropicOptions>(configuration.GetSection(AnthropicOptions.SectionName));
        services.Configure<CommunityKbSettings>(configuration.GetSection(CommunityKbSettings.SectionName));

        services.AddSingleton<IAgentSettingsStore, AgentSettingsStore>();
        services.AddSingleton<IAgentRateLimitStore, AgentRateLimitStore>();
        services.AddSingleton<IAgentRetentionRunStore, AgentRetentionRunStore>();

        services.AddScoped<IAgentRepository, AgentRepository>();
        services.AddScoped<AgentSettingsService>();
        services.AddScoped<IAgentSettingsService>(sp => sp.GetRequiredService<AgentSettingsService>());
        services.AddScoped<IAgentAvailability>(sp => sp.GetRequiredService<AgentSettingsService>());
        services.AddScoped<IAgentUserSnapshotProvider, AgentUserSnapshotProvider>();
        services.AddScoped<IAgentAdminStatusService, AgentAdminStatusService>();

        services.AddScoped<IAgentAnthropicBalanceProvider, AnthropicBalanceProvider>();

        // Singletons: stateless readers that own a per-stem MemoryCache slot via the shared
        // IMemoryCache. IGuideContentSource is registered as a singleton by AddGuideSection,
        // so capturing it here is safe.
        services.AddSingleton<AgentSectionDocReader>();
        services.AddSingleton<AgentFeatureSpecReader>();
        services.AddSingleton<GitHubCommunityKbContentSource>();
        services.AddSingleton<CommunityFaqReader>(sp => new CommunityFaqReader(
            sp.GetRequiredService<GitHubCommunityKbContentSource>(),
            sp.GetRequiredService<IMemoryCache>(),
            sp.GetRequiredService<ILogger<CommunityFaqReader>>()));
        services.AddSingleton<IAgentPreloadCorpusBuilder, AgentPreloadCorpusBuilder>();
        services.AddSingleton<IAgentPromptAssembler, AgentPromptAssembler>();
        services.AddSingleton<IAgentAbuseDetector, AgentAbuseDetector>();
        services.AddSingleton<IAnthropicClient, AnthropicClient>();

        services.AddScoped<IAgentToolDispatcher, AgentToolDispatcher>();
        services.AddScoped<AgentService>();
        services.AddScoped<IAgentService>(sp => sp.GetRequiredService<AgentService>());
        services.AddScoped<IAgentConversationRetention>(sp => sp.GetRequiredService<AgentService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<AgentService>());

        services.AddHostedService<AgentSettingsStoreWarmupHostedService>();
        services.AddHostedService<AgentPreloadWarmupHostedService>();

        // Resource-based rate limiting: the handler moves with the section, the requirement
        // with it (design §15 step 6). There is no named policy — AgentController passes the
        // requirement directly, the shape Store/Expenses/Containers already use.
        services.AddScoped<IAuthorizationHandler, AgentRateLimitHandler>();

        // Agent API key — gates GET /api/agent (read-only chat-history review).
        // Bound to its own env var so a leaked feedback/log key cannot read transcripts.
        services.Configure<AgentApiSettings>(opts =>
        {
            opts.ApiKey = Environment.GetEnvironmentVariable("AGENT_API_KEY") ?? string.Empty;
        });
        services.AddScoped<AgentApiKeyAuthFilter>();
    }
}
