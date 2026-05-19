using Markdig;
using Microsoft.Extensions.Options;
using Humans.Application.Constants;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;

namespace Humans.Infrastructure.Services;

public sealed class GuideRenderer(
    IOptions<GuideSettings> settings,
    GuideMarkdownPreprocessor preprocessor,
    GuideHtmlPostprocessor postprocessor) : IGuideRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public string Render(string markdown, string fileStem)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileStem);

        var wrapped = preprocessor.Wrap(markdown);
        var rendered = Markdown.ToHtml(wrapped, Pipeline);
        return postprocessor.Rewrite(rendered, settings.Value, GuideFiles.All);
    }
}
