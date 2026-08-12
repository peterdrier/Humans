using Humans.Application.Interfaces;

namespace Humans.Consent.Contracts;

public record LegalDocumentDefinition(string Slug, string DisplayName, string RepoFolder, string FilePrefix);

public interface ILegalDocumentService : IApplicationService
{
    IReadOnlyList<LegalDocumentDefinition> GetAvailableDocuments();
    Task<Dictionary<string, string>> GetDocumentContentAsync(string slug);
}
