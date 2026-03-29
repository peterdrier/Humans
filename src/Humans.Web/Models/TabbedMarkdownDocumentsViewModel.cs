namespace Humans.Web.Models;

public class TabbedMarkdownDocumentsViewModel
{
    public IReadOnlyList<KeyValuePair<string, string>> Documents { get; init; } = [];
    public string TabsId { get; init; } = "document-tabs";
    public string ContentId { get; init; } = "document-tabs-content";
    public string DefaultLanguage { get; init; } = string.Empty;
    public string EmptyMessage { get; init; } = string.Empty;
    public string TabsCssClass { get; init; } = "nav nav-tabs";
    public string ContentCssClass { get; init; } = "tab-content p-4";
    public string ContentStyle { get; init; } = "max-height: 500px; overflow-y: auto;";
    public bool UseLegalLanguageLabels { get; init; } = true;
}
