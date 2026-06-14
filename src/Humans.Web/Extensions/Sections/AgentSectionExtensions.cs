using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Stores;
using Humans.Application.Services.Agent;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories;
using Humans.Infrastructure.Services;
using Humans.Infrastructure.Services.Agent;
using Humans.Infrastructure.Services.Preload;
using Humans.Infrastructure.Stores;
using Humans.Web.Filters;
using Humans.Web.Services.Agent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Humans.Web.Extensions.Sections;

internal static class AgentSectionExtensions
{
    internal static IServiceCollection AddAgentSection(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AnthropicOptions>(configuration.GetSection(AnthropicOptions.SectionName));
        services.Configure<CommunityKbSettings>(configuration.GetSection(CommunityKbSettings.SectionName));

        services.AddSingleton<IAgentSettingsStore, AgentSettingsStore>();
        services.AddSingleton<IAgentRateLimitStore, AgentRateLimitStore>();
        services.AddSingleton<IAgentRetentionRunStore, AgentRetentionRunStore>();

        services.AddScoped<IAgentRepository, AgentRepository>();
        services.AddScoped<IAgentSettingsService, AgentSettingsService>();
        services.AddScoped<IAgentUserSnapshotProvider, AgentUserSnapshotProvider>();
        services.AddScoped<IAgentAdminStatusService, AgentAdminStatusService>();

        services.AddScoped<IAgentAnthropicBalanceProvider, Humans.Infrastructure.Services.Anthropic.AnthropicBalanceProvider>();

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
        services.AddSingleton<IAnthropicClient, Humans.Infrastructure.Services.Anthropic.AnthropicClient>();

        services.AddScoped<IAgentToolDispatcher, AgentToolDispatcher>();
        services.AddScoped<AgentService>();
        services.AddScoped<IAgentService>(sp => sp.GetRequiredService<AgentService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<AgentService>());

        services.AddSingleton<IAgentPreloadAugmentor, AgentPreloadAugmentor>();

        services.AddScoped<AgentConversationRetentionJob>();
        services.AddHostedService<AgentSettingsStoreWarmupHostedService>();
        services.AddHostedService<AgentPreloadWarmupHostedService>();

        // Agent API key — gates GET /api/agent (read-only chat-history review).
        // Bound to its own env var so a leaked feedback/log key cannot read transcripts.
        services.Configure<AgentApiSettings>(opts =>
        {
            opts.ApiKey = Environment.GetEnvironmentVariable("AGENT_API_KEY") ?? string.Empty;
        });
        services.AddScoped<AgentApiKeyAuthFilter>();

        return services;
    }
}
