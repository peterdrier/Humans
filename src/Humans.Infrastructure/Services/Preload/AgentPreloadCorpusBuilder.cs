using System.Text;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;

namespace Humans.Infrastructure.Services.Preload;

public sealed class AgentPreloadCorpusBuilder(
    AgentSectionDocReader sections,
    IMemoryCache cache,
    IAgentPreloadAugmentor? augmentor = null) : IAgentPreloadCorpusBuilder
{
    private static readonly IReadOnlyList<string> Tier1Sections =
        ["Onboarding", "Teams", "LegalAndConsent", "Governance", "Shifts", "Tickets", "Profiles", "Auth"];

    private static readonly IReadOnlyList<string> Tier2Sections =
        ["Onboarding", "Teams", "LegalAndConsent", "Governance", "Shifts", "Tickets", "Profiles", "Auth",
         "Budget", "Camps", "CityPlanning", "Campaigns", "Feedback", "GoogleIntegration"];

    public async Task<string> BuildAsync(AgentPreloadConfig config, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"agent:preload:{config}";
        if (cache.TryGetValue<string>(cacheKey, out var cached) && cached is not null)
            return cached;

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

        if (augmentor is not null)
        {
            sb.AppendLine(augmentor.BuildAccessMatrixMarkdown());
            sb.AppendLine();
            sb.AppendLine(augmentor.BuildGlossariesMarkdown());
            sb.AppendLine();
            sb.AppendLine(augmentor.BuildRouteMapMarkdown());
        }

        var result = sb.ToString();
        cache.Set(cacheKey, result, TimeSpan.FromMinutes(30));
        return result;
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
