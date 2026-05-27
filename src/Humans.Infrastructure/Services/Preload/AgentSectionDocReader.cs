using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;

namespace Humans.Infrastructure.Services.Preload;

/// <summary>
/// Reads a whitelisted <c>docs/sections/{key}.md</c> file from the Humans repo on GitHub
/// at runtime via the shared <see cref="IGuideContentSource"/>. Cached in memory with the
/// Guide TTL so per-tool-call round trips are avoided. Returns <c>null</c> on miss (unknown
/// key, GitHub 404, or transient fetch failure) so the caller can degrade gracefully.
/// </summary>
public sealed class AgentSectionDocReader(
    IGuideContentSource source,
    IMemoryCache cache,
    IOptions<GuideSettings> settings,
    ILogger<AgentSectionDocReader> logger)
{
    internal const string FolderPath = "docs/sections";
    private const string CacheKeyPrefix = "agent:section:";

    private static readonly HashSet<string> Whitelist =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Onboarding", "Teams", "LegalAndConsent", "Governance", "Shifts",
            "Tickets", "Profiles", "Auth", "Budget", "Camps",
            "CityPlanning", "Campaigns", "Feedback", "GoogleIntegration"
        };

    public async Task<string?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        // Resolve the caller-supplied key to the canonical-cased whitelist entry so the
        // GitHub path matches exactly (GitHub paths are case-sensitive). LLMs routinely
        // lowercase the key (e.g. "shifts"); the whitelist lookup is case-insensitive
        // but the fetched filename must be canonical ("Shifts.md").
        if (!Whitelist.TryGetValue(key, out var canonicalKey)) return null;

        var cacheKey = CacheKeyPrefix + canonicalKey;
        if (cache.TryGetValue<string>(cacheKey, out var cached) && cached is not null)
            return cached;

        try
        {
            var body = await source.GetMarkdownAsync(FolderPath, canonicalKey, cancellationToken);
            var ttl = TimeSpan.FromHours(Math.Max(1, settings.Value.CacheTtlHours));
            cache.Set(cacheKey, body, new MemoryCacheEntryOptions { SlidingExpiration = ttl });
            return body;
        }
        catch (NotFoundException)
        {
            // Whitelisted key but no file in the repo — treat as miss so the tool degrades
            // cleanly rather than crashing the dispatcher. Log per
            // memory/code/always-log-problems.md so a missing section guide is visible in
            // the prod log viewer (which only renders Warning+) instead of disappearing.
            logger.LogWarning("Section guide {Section} not found on GitHub (docs/sections)", canonicalKey);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to fetch agent section guide {Section} from GitHub; returning null", canonicalKey);
            return null;
        }
    }

    public IReadOnlySet<string> KnownSections => Whitelist;
}
