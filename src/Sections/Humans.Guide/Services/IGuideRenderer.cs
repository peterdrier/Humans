namespace Humans.Guide.Services;

/// <summary>
/// Renders already-filtered guide markdown to HTML and rewrites its links and images. Pure
/// function of (markdown, file stem).
/// </summary>
internal interface IGuideRenderer
{
    string Render(string markdown, string fileStem);
}
