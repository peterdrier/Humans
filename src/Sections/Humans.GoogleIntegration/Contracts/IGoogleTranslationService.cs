using Humans.Base.Interfaces;
namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// GoogleIntegration-section service exposing machine translation to other sections (spec
/// 2026-06-03 §6.1: Survey's "pre-fill translations" authoring assist). Cross-section callers
/// inject this interface, never <see cref="Humans.GoogleIntegration.Services.Workspace.IGoogleTranslationClient"/>.
/// </summary>
public interface IGoogleTranslationService : IApplicationService
{
    /// <inheritdoc cref="Humans.GoogleIntegration.Services.Workspace.IGoogleTranslationClient.TranslateAsync"/>
    Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken ct = default);
}
