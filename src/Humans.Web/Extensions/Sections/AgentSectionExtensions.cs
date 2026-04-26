using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Stores;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.HostedServices;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories;
using Humans.Infrastructure.Services.Agent;
using Humans.Infrastructure.Services.Preload;
using Humans.Infrastructure.Stores;
using Humans.Web.Services.Agent;

namespace Humans.Web.Extensions.Sections;

internal static class AgentSectionExtensions
{
    internal static IServiceCollection AddAgentSection(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AnthropicOptions>(configuration.GetSection(AnthropicOptions.SectionName));

        services.AddSingleton<IAgentSettingsStore, AgentSettingsStore>();
        services.AddSingleton<IAgentRateLimitStore, AgentRateLimitStore>();

        services.AddScoped<IAgentConversationRepository, AgentConversationRepository>();
        services.AddScoped<IAgentSettingsService, AgentSettingsService>();
        services.AddScoped<IAgentUserSnapshotProvider, AgentUserSnapshotProvider>();

        services.AddSingleton<AgentSectionDocReader>();
        services.AddSingleton<AgentFeatureSpecReader>();
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

        return services;
    }
}
