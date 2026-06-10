using Humans.Application.Interfaces.GoogleIntegration;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services.GoogleWorkspace;

/// <summary>
/// Dev/test <see cref="IGoogleTranslationClient"/> used when no Google credentials are configured
/// (per the §15 connector pattern there is no stub *service* — the Application-layer service runs
/// against this). Returns the input prefixed with the target language so the translate flow is
/// exercisable end-to-end and the fake output is unmistakable.
/// </summary>
public sealed class StubGoogleTranslationClient(ILogger<StubGoogleTranslationClient> logger) : IGoogleTranslationClient
{
    public Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken ct = default)
    {
        logger.LogDebug(
            "[STUB] Translate {Count} segments {Source}→{Target}", texts.Count, sourceLanguage, targetLanguage);
        return Task.FromResult<IReadOnlyList<string>>(texts.Select(t => $"[{targetLanguage}] {t}").ToList());
    }
}
