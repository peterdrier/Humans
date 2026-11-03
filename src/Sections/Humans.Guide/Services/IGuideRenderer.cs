namespace Humans.Guide.Services;

/// <summary>Renders already-filtered markdown.</summary>
internal interface IGuideRenderer
{
    string Render(string markdown);
}
