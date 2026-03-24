namespace Humans.Application.Interfaces;

public record LegalDocumentDefinition(string Slug, string DisplayName, string RepoFolder, string FilePrefix);

public interface ILegalDocumentService
{
    IReadOnlyList<LegalDocumentDefinition> GetAvailableDocuments();
    Task<Dictionary<string, string>> GetDocumentContentAsync(string slug);
}
