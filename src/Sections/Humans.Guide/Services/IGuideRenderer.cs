namespace Humans.Guide.Services;

/// <summary>
/// Renders guide markdown to HTML with role-section div wrappers and rewritten
/// links/images. Pure function of (markdown, file stem) — safe to cache the output.
/// </summary>
internal interface IGuideRenderer
{
    string Render(string markdown, string fileStem);
}
