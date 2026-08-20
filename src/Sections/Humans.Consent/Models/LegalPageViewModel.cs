using Humans.Consent.Contracts;

using Humans.Base.Models;

namespace Humans.Consent.Models;

internal sealed class LegalPageViewModel
{
    public required IReadOnlyList<LegalDocumentDefinition> AllDocuments { get; init; }
    public required string CurrentSlug { get; init; }
    public required string CurrentDocumentName { get; init; }
    public required TabbedMarkdownDocumentsViewModel DocumentContent { get; init; }
}
