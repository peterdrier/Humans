namespace Humans.Guide.Services;

/// <summary>Pure function of (markdown, file stem). The markdown is already filtered.</summary>
internal interface IGuideRenderer
{
    string Render(string markdown, string fileStem);
}
