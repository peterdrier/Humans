namespace Humans.Web.Models;

public sealed record GuideSidebarEntry(string Stem, string DisplayName, string Group);

public sealed class GuideSidebarModel
{
    public required IReadOnlyList<GuideSidebarEntry> Entries { get; init; }
    public required string? ActiveStem { get; init; }
}
