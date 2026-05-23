using Microsoft.Extensions.Hosting;

namespace Humans.Infrastructure.Services.Preload;

/// <summary>Reads a whitelisted <c>docs/sections/{key}.md</c> file relative to the content root.</summary>
public sealed class AgentSectionDocReader(IHostEnvironment env)
{
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
        // on-disk filename matches exactly on case-sensitive filesystems (Linux deployment).
        // LLMs routinely lowercase the key (e.g. "shifts"), which clears the case-insensitive
        // whitelist but would otherwise miss "Shifts.md" via a case-sensitive File.Exists.
        if (!Whitelist.TryGetValue(key, out var canonicalKey)) return null;
        var path = Path.Combine(env.ContentRootPath, "docs", "sections", $"{canonicalKey}.md");
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path, cancellationToken);
    }

    public IReadOnlySet<string> KnownSections => Whitelist;
}
