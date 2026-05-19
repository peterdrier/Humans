using Microsoft.Extensions.Hosting;

namespace Humans.Infrastructure.Services.Preload;

public sealed class AgentFeatureSpecReader(IHostEnvironment env)
{
    public async Task<string?> ReadAsync(string stem, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stem) ||
            stem.Any(c => !(char.IsLetterOrDigit(c) || c == '-' || c == '_')))
            return null;

        var path = Path.Combine(env.ContentRootPath, "docs", "features", $"{stem}.md");
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path, cancellationToken);
    }
}
