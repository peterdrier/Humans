namespace Humans.Agent.Services.Preload;

/// <summary>Produces the non-section chunks (access matrix, section-help glossaries, route map,
/// FAQ) that round out the cacheable preload, rendered from <c>Humans.Base</c>'s shared help
/// registries.</summary>
internal interface IAgentPreloadAugmentor
{
    string BuildAccessMatrixMarkdown();
    string BuildGlossariesMarkdown();
    string BuildRouteMapMarkdown();
    string BuildFaqMarkdown();
}
