namespace Humans.Web.Models;

/// <summary>
/// One drawing in the logo library: wwwroot/img/logo-library/&lt;Slug&gt;/entry.json beside its logo.svg,
/// written by the /redraw-logo skill. Effort and Brief are null for rounds drawn before they were recorded.
/// </summary>
public sealed record LogoLibraryEntry
{
    public string Slug { get; init; } = string.Empty;
    public string Date { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string? Effort { get; init; }
    public string? Brief { get; init; }
    public string Notes { get; init; } = string.Empty;
}
