using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Humans.Agent.Contracts;
using Humans.Agent.Domain;

namespace Humans.Agent.Services.Preload;

/// <summary>
/// Assembles the agent's preload corpus for a given <see cref="AgentPreloadConfig"/> tier —
/// section docs, the community FAQ and the Shell-owned augmentor blocks — and caches the result
/// in <see cref="IMemoryCache"/> until an admin-triggered reload swaps it.
/// </summary>
/// <remarks>
/// <see cref="IAgentPreloadAugmentor"/> is required, not optional. It used to default to
/// <c>null</c> with a <c>is not null</c> guard around its four blocks — which meant a missing
/// Shell registration produced a corpus quietly stripped of the access matrix, the glossaries,
/// the route map and the FAQ, with no startup failure and no log line. Required makes DI fail
/// loudly instead (peterdrier/Humans#1259).
/// </remarks>
internal sealed class AgentPreloadCorpusBuilder(
    AgentSectionDocReader sections,
    CommunityFaqReader community,
    IMemoryCache cache,
    IAgentPreloadAugmentor augmentor) : IAgentPreloadCorpusBuilder
{
    private static readonly IReadOnlyList<string> Tier1Sections =
        ["Onboarding", "Teams", "Consent", "Governance", "Shifts", "Tickets", "Users", "Auth"];

    // Keep in step with AgentSectionKeys.All — a section the reader can serve but
    // that never appears in this index is one the agent has no reason to ask for.
    private static readonly IReadOnlyList<string> Tier2Sections =
        ["Onboarding", "Teams", "Consent", "Governance", "Shifts", "Tickets", "Users", "Auth",
         "Budget", "Camps", "CityPlanning", "Campaigns", "Feedback", "GoogleIntegration",
         "Events", "Guide", "Store", "Scanner", "Gate", "Calendar", "Cantina", "Containers", "Issues"];

    private static readonly MemoryCacheEntryOptions HoldForever =
        new() { Priority = CacheItemPriority.NeverRemove };

    public async Task<string> BuildAsync(AgentPreloadConfig config, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"agent:preload:{config}";
        if (cache.TryGetValue<string>(cacheKey, out var cached) && cached is not null)
            return cached;

        var result = await BuildCorpusAsync(config, cancellationToken);
        cache.Set(cacheKey, result, HoldForever);
        return result;
    }

    public async Task ReloadAllAsync(CancellationToken cancellationToken = default)
    {
        // Refresh the KB source first so the rebuilt index reflects the latest repo state,
        // then rebuild + atomically overwrite every tier's cached corpus (reload + swap).
        await community.ReloadAsync(cancellationToken);
        foreach (var config in Enum.GetValues<AgentPreloadConfig>())
        {
            var fresh = await BuildCorpusAsync(config, cancellationToken);
            cache.Set($"agent:preload:{config}", fresh, HoldForever);
        }
    }

    private async Task<string> BuildCorpusAsync(AgentPreloadConfig config, CancellationToken cancellationToken)
    {
        var sections1 = config == AgentPreloadConfig.Tier1 ? Tier1Sections : Tier2Sections;
        var sb = new StringBuilder();
        sb.AppendLine("# Nobodies Collective — System Knowledge");
        sb.AppendLine();
        sb.AppendLine("Below is the section index for the Humans system: each entry has a section key and a one-line summary. The full invariants doc for any section is fetched on demand via the `fetch_section_guide` tool — do NOT answer substantive questions from this index alone.");
        sb.AppendLine();
        sb.AppendLine("## Section Index");
        sb.AppendLine();
        foreach (var key in sections1)
        {
            var body = await sections.ReadAsync(key, cancellationToken);
            if (body is null) continue;
            var tagline = ExtractTagline(body);
            sb.Append("- **").Append(key).Append("** — ").AppendLine(tagline);
        }
        sb.AppendLine();

        sb.AppendLine(augmentor.BuildAccessMatrixMarkdown());
        sb.AppendLine();
        sb.AppendLine(augmentor.BuildGlossariesMarkdown());
        sb.AppendLine();
        sb.AppendLine(augmentor.BuildRouteMapMarkdown());
        sb.AppendLine();
        sb.AppendLine(augmentor.BuildFaqMarkdown());

        var communityEntries = await community.ListTopicsAsync(cancellationToken);
        if (communityEntries.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Community FAQ (community-sourced — unofficial, may be outdated)");
            sb.AppendLine();
            sb.AppendLine("Crowd-sourced answers from the community Discord about the Elsewhere event, the association, on-site life, and community practices. Match the user's question — and any expanded abbreviations/jargon — against each topic's `covers:` keywords, then fetch the relevant topic(s) on demand with the `fetch_community_faq` tool (topic=<key>). Always tell the user these answers are community discussion, not official.");
            sb.AppendLine();
            foreach (var entry in communityEntries)
            {
                sb.Append("- **").Append(entry.Topic).Append("** — ").Append(entry.Summary);
                if (entry.Keywords.Length > 0)
                    sb.Append(" — covers: ").Append(entry.Keywords);
                if (entry.LastUpdated is not null)
                    sb.Append(" (last updated ").Append(entry.LastUpdated).Append(')');
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string ExtractTagline(string body)
    {
        bool foundH1 = false;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!foundH1)
            {
                if (line.StartsWith("# ", StringComparison.Ordinal)) foundH1 = true;
                continue;
            }
            if (line.Length == 0) continue;
            return line;
        }
        return "";
    }
}
