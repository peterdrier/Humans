namespace Humans.Web.Models;

/// <summary>
/// One drawing in the logo library: wwwroot/img/logo-library/&lt;Slug&gt;/entry.json beside its logo.svg,
/// written by the /redraw-logo skill. Effort and Brief are null for rounds drawn before they were recorded.
/// Notes carry every supported culture inside the entry, so parallel entries never share a resource file.
/// </summary>
public sealed record LogoLibraryEntry
{
    public string Slug { get; init; } = string.Empty;
    public string Date { get; init; } = string.Empty;
    public string Family { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string? Effort { get; init; }
    public string? Brief { get; init; }
    public IReadOnlyDictionary<string, string> Notes { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public string NotesIn(string cultureCode) =>
        Notes.GetValueOrDefault(cultureCode) ?? Notes.GetValueOrDefault("en") ?? string.Empty;
}
