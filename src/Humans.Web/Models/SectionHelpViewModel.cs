namespace Humans.Web.Models;

public class SectionHelpViewModel
{
    public required string SectionKey { get; init; }
    public required string SectionName { get; init; }
    public string? GuideMarkdown { get; init; }
    public string? GlossaryMarkdown { get; init; }
    public AccessMatrixData? AccessMatrix { get; init; }
}
