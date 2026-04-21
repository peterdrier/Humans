using Microsoft.Extensions.Hosting;

namespace Humans.Infrastructure.Services.Preload;

public sealed class AgentFeatureSpecReader
{
    private readonly IHostEnvironment _env;

    public AgentFeatureSpecReader(IHostEnvironment env) => _env = env;

    public async Task<string?> ReadAsync(string stem, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stem) ||
            stem.Any(c => !(char.IsLetterOrDigit(c) || c == '-' || c == '_')))
            return null;

        var path = Path.Combine(_env.ContentRootPath, "docs", "features", $"{stem}.md");
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path, cancellationToken);
    }
}
