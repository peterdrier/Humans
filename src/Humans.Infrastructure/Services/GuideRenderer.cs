using Markdig;
using Microsoft.Extensions.Options;
using Humans.Application.Constants;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;

namespace Humans.Infrastructure.Services;

public sealed class GuideRenderer : IGuideRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private readonly IOptions<GuideSettings> _settings;
    private readonly GuideMarkdownPreprocessor _preprocessor;
    private readonly GuideHtmlPostprocessor _postprocessor;

    public GuideRenderer(
        IOptions<GuideSettings> settings,
        GuideMarkdownPreprocessor preprocessor,
        GuideHtmlPostprocessor postprocessor)
    {
        _settings = settings;
        _preprocessor = preprocessor;
        _postprocessor = postprocessor;
    }

    public string Render(string markdown, string fileStem)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileStem);

        var wrapped = _preprocessor.Wrap(markdown);
        var rendered = Markdown.ToHtml(wrapped, Pipeline);
        return _postprocessor.Rewrite(rendered, _settings.Value, GuideFiles.All);
    }
}
