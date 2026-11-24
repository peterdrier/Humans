using System.Text;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace Humans.Agent.Services.Preload;

/// <summary>
/// Cached until an admin reload swaps it. <see cref="IAgentPreloadAugmentor"/> is required, so a missing registration
/// fails DI loudly instead of building a corpus without the access matrix, glossaries, route map and FAQ.
/// </summary>
internal sealed class AgentPreloadCorpusBuilder(
    AgentSectionDocReader sections,
    CommunityFaqReader community,
    IMemoryCache cache,
    IAgentPreloadAugmentor augmentor,
    ISectionSwitches? switches = null) : IAgentPreloadCorpusBuilder
{
    private static readonly MemoryCacheEntryOptions HoldForever =
        new() { Priority = CacheItemPriority.NeverRemove };

    private readonly Lock _cacheGate = new();
    private long _cacheGeneration;

    // The index leaves out sections switched off for the event, so the helper does not explain pages that answer 404.
    // Keyed on the switched-off set, so switching a section on or off builds a fresh corpus.
    private static string CacheKey(IReadOnlySet<string> switchedOff) =>
        switchedOff.Count == 0
            ? "agent:preload"
            : $"agent:preload:{string.Join(",", switchedOff.Order(StringComparer.OrdinalIgnoreCase))}";

    private async Task<IReadOnlySet<string>> SwitchedOffAsync(CancellationToken cancellationToken) =>
        switches is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : await switches.GetSwitchedOffAsync(cancellationToken);

    public async Task<string> BuildAsync(CancellationToken cancellationToken = default)
    {
        var switchedOff = await SwitchedOffAsync(cancellationToken);
        var cacheKey = CacheKey(switchedOff);
        long generation;
        lock (_cacheGate)
        {
            if (cache.TryGetValue<string>(cacheKey, out var cached) && cached is not null)
                return cached;
            generation = _cacheGeneration;
        }

        var (result, isComplete) = await BuildCorpusAsync(switchedOff, cancellationToken);
        lock (_cacheGate)
        {
            if (isComplete && generation == _cacheGeneration)
                cache.Set(cacheKey, result, HoldForever);
        }
        return result;
    }

    public async Task<bool> ReloadAllAsync(CancellationToken cancellationToken = default)
    {
        // Refresh the Community FAQ first so the rebuilt index reflects the latest repo state.
        if (!await community.ReloadAsync(cancellationToken)) return false;
        var switchedOff = await SwitchedOffAsync(cancellationToken);
        var (fresh, isComplete) = await BuildCorpusAsync(switchedOff, cancellationToken);
        if (!isComplete) return false;
        lock (_cacheGate)
        {
            _cacheGeneration++;
            cache.Set(CacheKey(switchedOff), fresh, HoldForever);
        }
        return true;
    }

    private async Task<(string Body, bool IsComplete)> BuildCorpusAsync(
        IReadOnlySet<string> switchedOff, CancellationToken cancellationToken)
    {
        var isComplete = true;
        var sb = new StringBuilder();
        sb.AppendLine("# Nobodies Collective — System Knowledge");
        sb.AppendLine();
        sb.AppendLine("Below is the section index for the Humans system: each entry has a section key and a one-line summary. The full section doc for any section is fetched on demand via the `fetch_section_doc` tool — do NOT answer substantive questions from this index alone.");
        sb.AppendLine();
        sb.AppendLine("## Section Index");
        sb.AppendLine();
        foreach (var key in sections.KnownSections.Where(key => !switchedOff.Contains(key)))
        {
            var body = await sections.ReadAsync(key, cancellationToken);
            if (body is null)
            {
                isComplete = false;
                continue;
            }
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

        var (communityEntries, communityComplete) = await community.ListTopicsAsync(cancellationToken);
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

        return (sb.ToString(), isComplete && communityComplete);
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
