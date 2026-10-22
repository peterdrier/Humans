using Humans.Agent.Authorization;
using Humans.Agent.Contracts;
using Humans.Agent.Data;
using Humans.Agent.Jobs;
using Humans.Agent.Services;
using Humans.Agent.Services.Anthropic;
using Humans.Agent.Services.Preload;
using Humans.Agent.Services.Stores;
using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Base.Configuration;
using Humans.Base.Hosting;
using Humans.Base.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Agent;

/// <summary>
/// The Anthropic connector lives here, not in Base: <c>IAnthropicClient</c> streams the section's own
/// <c>AgentTurnToken</c>.
/// </summary>
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

        // Stateless readers over the shared IMemoryCache; IGuideContentSource is a singleton, so capturing it is safe.
        services.AddSingleton<AgentSectionDocReader>();
        services.AddSingleton<AgentFeatureSpecReader>();
        services.AddSingleton<GitHubCommunityKbContentSource>();
        services.AddSingleton<CommunityFaqReader>(sp => new CommunityFaqReader(
            sp.GetRequiredService<GitHubCommunityKbContentSource>(),
            sp.GetRequiredService<IMemoryCache>(),
            sp.GetRequiredService<ILogger<CommunityFaqReader>>()));
        services.AddSingleton<IAgentPreloadAugmentor, AgentPreloadAugmentor>();
        services.AddSingleton<IAgentPreloadCorpusBuilder, AgentPreloadCorpusBuilder>();
        services.AddSingleton<IAgentPromptAssembler, AgentPromptAssembler>();
        services.AddSingleton<IAgentAbuseDetector, AgentAbuseDetector>();
        services.AddSingleton<IAnthropicClient, AnthropicClient>();

        services.AddScoped<IAgentToolDispatcher, AgentToolDispatcher>();
        services.AddScoped<AgentService>();
        services.AddScoped<IAgentService>(sp => sp.GetRequiredService<AgentService>());
        services.AddScoped<IAgentConversationRetention>(sp => sp.GetRequiredService<AgentService>());
        services.AddScoped<IAgentTranscriptRead>(sp => sp.GetRequiredService<AgentService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<AgentService>());

        services.AddHostedService<AgentSettingsStoreWarmupHostedService>();
        services.AddHostedService<AgentPreloadWarmupHostedService>();

        services.AddScoped<IAuthorizationHandler, AgentRateLimitHandler>();

        services.AddScoped<AgentConversationRetentionJob>();
    }
}
