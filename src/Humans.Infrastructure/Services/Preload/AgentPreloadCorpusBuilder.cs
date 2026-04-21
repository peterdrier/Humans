using System.Text;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;

namespace Humans.Infrastructure.Services.Preload;

public sealed class AgentPreloadCorpusBuilder : IAgentPreloadCorpusBuilder
{
    private static readonly IReadOnlyList<string> Tier1Sections =
        ["Onboarding", "Teams", "LegalAndConsent", "Governance", "Shifts", "Tickets", "Profiles", "Admin"];

    private static readonly IReadOnlyList<string> Tier2Sections =
        ["Onboarding", "Teams", "LegalAndConsent", "Governance", "Shifts", "Tickets", "Profiles", "Admin",
         "Budget", "Camps", "CityPlanning", "Campaigns", "Feedback", "GoogleIntegration"];

    private readonly AgentSectionDocReader _sections;
    private readonly IMemoryCache _cache;

    public AgentPreloadCorpusBuilder(AgentSectionDocReader sections, IMemoryCache cache)
    {
        _sections = sections;
        _cache = cache;
    }

    public async Task<string> BuildAsync(AgentPreloadConfig config, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"agent:preload:{config}";
        if (_cache.TryGetValue<string>(cacheKey, out var cached) && cached is not null)
            return cached;

        var sections = config == AgentPreloadConfig.Tier1 ? Tier1Sections : Tier2Sections;
        var sb = new StringBuilder();
        sb.AppendLine("# Nobodies Collective — System Knowledge");
        sb.AppendLine();
        sb.AppendLine("The following is the canonical operational documentation for the Humans system. Use it verbatim when answering questions; do not invent rules, routes, or role names not present here.");
        sb.AppendLine();
        foreach (var key in sections)
        {
            var body = await _sections.ReadAsync(key, cancellationToken);
            if (body is null) continue;
            sb.AppendLine($"# {key}");
            sb.AppendLine(body);
            sb.AppendLine();
        }

        var result = sb.ToString();
        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(30));
        return result;
    }
}
