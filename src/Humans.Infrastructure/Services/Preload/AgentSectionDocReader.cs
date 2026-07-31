using System.Diagnostics.CodeAnalysis;
using Humans.Application.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Octokit;

namespace Humans.Infrastructure.Services.Preload;

/// <summary>
/// Reads a whitelisted <c>docs/sections/{key}.md</c> file from the Humans repo on GitHub
/// at runtime via the shared <see cref="IGuideContentSource"/>. Held in memory with no
/// expiration (loaded once at startup or first call, refreshed only on restart) so
/// per-tool-call round trips are avoided. Returns <c>null</c> on miss (unknown
/// key, GitHub 404, or transient fetch failure) so the caller can degrade gracefully.
/// </summary>
public sealed class AgentSectionDocReader(
    IGuideContentSource source,
    IMemoryCache cache,
    ILogger<AgentSectionDocReader> logger)
{
    internal const string FolderPath = "docs/sections";
    private const string CacheKeyPrefix = "agent:section:";

    // Every user-facing section. A section left off this list is unreachable: the agent
    // either refuses the question outright or answers it from the community Discord FAQ
    // with an "unofficial, may be outdated" disclaimer — even for a first-party feature.
    // Operator/internal-only sections (Finance, Holded, Email, Mailer, AuditLog, Debug,
    // admin-shell) stay off deliberately; add one when triage shows users asking about it.
    private static readonly HashSet<string> Whitelist =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Onboarding", "Teams", "LegalAndConsent", "Governance", "Shifts",
            "Tickets", "Profiles", "Auth", "Budget", "Camps",
            "CityPlanning", "Campaigns", "Feedback", "GoogleIntegration",
            "Events", "Guide", "Store", "Scanner", "Gate",
            "Calendar", "Cantina", "Containers", "Issues"
        };

    // Names the model reads out of its own preload corpus (help-widget glossary keys and
    // access-matrix display names) and out of user jargon, mapped onto the section file they
    // are actually about. These are a different namespace from the whitelist: the model names
    // the right section in prose and still dead-ends on the tool call. 20 such calls across 9
    // production conversations, three of which ended in an empty reply to the user
    // (nobodies-collective/Humans#949). Every value must be a whitelist key.
    private static readonly Dictionary<string, string> Aliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Profile"] = "Profiles",
            ["OnboardingReview"] = "Onboarding",
            // /Admin is a nav holder, not a section; its help glossary is governance/ops terms.
            ["Admin"] = "Governance",
            ["Board"] = "Governance",
            // A barrio is a camp — Camps.md defines them; CityPlanning.md only places them.
            ["Barrios"] = "Camps",
            ["CityPlanningOverview"] = "CityPlanning",
            ["CityPlanningBarrioMap"] = "CityPlanning",
            ["ContainerMap"] = "Containers",
        };

    // No expiration + NeverRemove: GitHub-backed content that only changes at release.
    // Loaded once (startup warm-up or first call) and held for the process lifetime.
    private static readonly MemoryCacheEntryOptions HoldForever =
        new() { Priority = CacheItemPriority.NeverRemove };

    /// <summary>
    /// Resolves a caller-supplied section key — canonical, any casing, or a known alias — to the
    /// whitelisted key whose <c>docs/sections/{key}.md</c> file backs it. Casing matters because
    /// GitHub paths are case-sensitive and LLMs routinely lowercase the key (e.g. "shifts"), so
    /// the fetched filename must be the canonical one ("Shifts.md").
    /// </summary>
    public static bool TryResolveKey(string key, [NotNullWhen(true)] out string? canonicalKey) =>
        Whitelist.TryGetValue(key, out canonicalKey) || Aliases.TryGetValue(key, out canonicalKey);

    public async Task<string?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        if (!TryResolveKey(key, out var canonicalKey)) return null;

        var cacheKey = CacheKeyPrefix + canonicalKey;
        if (cache.TryGetValue<string>(cacheKey, out var cached) && cached is not null)
            return cached;

        try
        {
            var body = await source.GetMarkdownAsync(FolderPath, canonicalKey, cancellationToken);
            cache.Set(cacheKey, body, HoldForever);
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
