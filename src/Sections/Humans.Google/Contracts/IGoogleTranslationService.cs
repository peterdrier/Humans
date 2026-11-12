using Humans.Base.Interfaces;
namespace Humans.Google.Contracts;

public interface IGoogleTranslationService : IApplicationService
{
    /// <inheritdoc cref="Humans.Google.Services.Workspace.IGoogleTranslationClient.TranslateAsync"/>
    Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken ct = default);
}
