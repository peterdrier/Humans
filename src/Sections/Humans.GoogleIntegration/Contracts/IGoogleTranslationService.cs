using Humans.Base.Interfaces;
namespace Humans.GoogleIntegration.Contracts;

public interface IGoogleTranslationService : IApplicationService
{
    /// <inheritdoc cref="Humans.GoogleIntegration.Services.Workspace.IGoogleTranslationClient.TranslateAsync"/>
    Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken ct = default);
}
