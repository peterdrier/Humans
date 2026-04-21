namespace Humans.Application.Interfaces;

/// <summary>Produces the non-section chunks (access matrix, section-help glossaries, route map)
/// that round out the cacheable preload. Implementation lives in the Web layer because those
/// sources live there.</summary>
public interface IAgentPreloadAugmentor
{
    string BuildAccessMatrixMarkdown();
    string BuildGlossariesMarkdown();
    string BuildRouteMapMarkdown();
}
