using Humans.Agent.Authorization;
using Humans.Agent.Contracts;
using Humans.Agent.Data;
using Humans.Agent.Health;
using Humans.Agent.Jobs;
using Humans.Agent.Services;
using Humans.Agent.Services.Anthropic;
using Humans.Agent.Services.Preload;
using Humans.Agent.Services.Stores;
using Humans.Agent.ViewComponents;
using Humans.Base.Authorization;
using Humans.Base.Configuration;
using Humans.Base.Constants;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Services;
using Humans.Gdpr.Contracts;
using Humans.Settings.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.AuditLog.Contracts;

namespace Humans.Agent;

/// <summary>
/// The Anthropic connector lives here, not in Base: <c>IAnthropicClient</c> streams the section's own
/// <c>AgentTurnToken</c>.
/// </summary>
public sealed class Section :
    ISection,
    ISectionAdminNav,
    ISectionAnnotations,
    ISectionChrome,
    ISectionConfiguration,
    ISectionHealthChecks,
    ISectionJobs,
    ISectionSettings
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, AgentAuditActions>();
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

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Agent", [
            new("Status",  "AdminAgent", "Status",        null, null, "fa-solid fa-gauge-high", PolicyNames.BoardOrAdmin),
            new("History", "Agent",      "Conversations", null, null, "fa-solid fa-comments",   PolicyNames.BoardOrAdmin)
        ])
    ];

    IEnumerable<SectionAnnotation> ISectionAnnotations.Annotations() =>
        AgentSectionKeys.All.Select(key => new SectionAnnotation(
            key,
            "Agent doc key",
            $"{AgentSectionDocReader.SectionProjectFolder(key)}/{key}.md"));

    IEnumerable<ChromeComponent> ISectionChrome.Components() =>
        [
            new(ChromeSlots.BodyEnd, typeof(HelpWidgetViewComponent)),
            new(ChromeSlots.WidgetGallery, typeof(AgentGalleryViewComponent)),
        ];

    void ISectionConfiguration.DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetOptionalSetting(registry, "Anthropic:ApiKey", "Anthropic", isSensitive: true,
            importance: ConfigurationImportance.Recommended);
        configuration.GetOptionalSetting(registry, "Anthropic:DefaultModel", "Anthropic");

        configuration.GetOptionalSetting(registry, "CommunityKb:Owner", "Community KB");
        configuration.GetOptionalSetting(registry, "CommunityKb:Repository", "Community KB");
        configuration.GetOptionalSetting(registry, "CommunityKb:Branch", "Community KB");
        configuration.GetOptionalSetting(registry, "CommunityKb:AccessToken", "Community KB", isSensitive: true);
    }

    void ISectionHealthChecks.AddHealthChecks(IHealthChecksBuilder builder, IConfiguration configuration)
    {
        builder.AddCheck<AnthropicHealthCheck>("anthropic-api-reachable", tags: [HealthCheckTags.External]);
        builder.AddCheck<AgentDocsHealthCheck>("agent-grounding-docs");
    }

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "agent-conversation-retention", typeof(AgentConversationRetentionJob), "15 3 * * *");
    }

    IEnumerable<SettingsTab> ISectionSettings.Tabs() =>
    [
        new SettingsTab("agent", "Settings_TabAgent", typeof(AgentSettingsTabViewComponent), PolicyNames.AdminOnly)
    ];
}
