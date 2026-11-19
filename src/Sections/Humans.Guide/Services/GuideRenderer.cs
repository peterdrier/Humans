using Markdig;
using Microsoft.Extensions.Options;
using Humans.Base.Configuration;

namespace Humans.Guide.Services;

internal sealed class GuideRenderer(
    IOptions<GuideSettings> settings,
    GuideHtmlPostprocessor postprocessor) : IGuideRenderer
{
    internal static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public string Render(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        return postprocessor.Rewrite(Markdown.ToHtml(markdown, Pipeline), settings.Value);
    }
}
