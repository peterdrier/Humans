namespace Humans.Guide.Models;

internal sealed record GuideSidebarEntry(string Stem, string DisplayName, string Group);

internal sealed class GuideSidebarModel
{
    public required IReadOnlyList<GuideSidebarEntry> Entries { get; init; }
    public required string? ActiveStem { get; init; }
}
