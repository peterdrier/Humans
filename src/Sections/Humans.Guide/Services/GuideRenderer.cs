using Markdig;
using Microsoft.Extensions.Options;
using Humans.Base.Configuration;

namespace Humans.Guide.Services;

internal sealed class GuideRenderer(
    IOptions<GuideSettings> settings,
    GuideHtmlPostprocessor postprocessor) : IGuideRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public string Render(string markdown, string fileStem)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileStem);

        return postprocessor.Rewrite(Markdown.ToHtml(markdown, Pipeline), settings.Value);
    }
}
