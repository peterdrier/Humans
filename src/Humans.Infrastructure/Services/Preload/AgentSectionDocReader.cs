using Microsoft.Extensions.Hosting;

namespace Humans.Infrastructure.Services.Preload;

/// <summary>Reads a whitelisted <c>docs/sections/{key}.md</c> file relative to the content root.</summary>
public sealed class AgentSectionDocReader
{
    private static readonly IReadOnlySet<string> Whitelist =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Onboarding", "Teams", "LegalAndConsent", "Governance", "Shifts",
            "Tickets", "Profiles", "Auth", "Budget", "Camps",
            "CityPlanning", "Campaigns", "Feedback", "GoogleIntegration"
        };

    private readonly IHostEnvironment _env;

    public AgentSectionDocReader(IHostEnvironment env) => _env = env;

    public async Task<string?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        if (!Whitelist.Contains(key)) return null;
        var path = Path.Combine(_env.ContentRootPath, "docs", "sections", $"{key}.md");
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path, cancellationToken);
    }

    public IReadOnlySet<string> KnownSections => Whitelist;
}
