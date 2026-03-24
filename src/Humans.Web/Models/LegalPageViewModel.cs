using Humans.Application.Interfaces;

namespace Humans.Web.Models;

public class LegalPageViewModel
{
    public required IReadOnlyList<LegalDocumentDefinition> AllDocuments { get; init; }
    public required string CurrentSlug { get; init; }
    public required string CurrentDocumentName { get; init; }
    public required TabbedMarkdownDocumentsViewModel DocumentContent { get; init; }
}
