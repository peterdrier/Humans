namespace Humans.Application.Interfaces.GoogleIntegration;

/// <summary>
/// GoogleIntegration-section service exposing machine translation to other sections (spec
/// 2026-06-03 §6.1: Survey's "pre-fill translations" authoring assist). Cross-section callers
/// inject this interface, never <see cref="IGoogleTranslationClient"/>.
/// </summary>
public interface IGoogleTranslationService : IApplicationService
{
    /// <inheritdoc cref="IGoogleTranslationClient.TranslateAsync"/>
    Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken ct = default);
}
