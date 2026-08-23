using Ganss.Xss;
using Markdig;

namespace Humans.Base.Extensions;

/// <summary>
/// Canonical Markdown-to-HTML rendering for authored content displayed by Humans.
/// </summary>
public static class SanitizedMarkdownRenderer
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    public static string Render(string? markdown, bool allowImages = true)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var rendered = Markdown.ToHtml(markdown, MarkdownPipeline);
        var sanitizer = new HtmlSanitizer();

        // Allow task list checkboxes rendered by Markdig's UseTaskLists extension.
        sanitizer.AllowedTags.Add("input");
        sanitizer.AllowedAttributes.Add("type");
        sanitizer.AllowedAttributes.Add("checked");
        sanitizer.AllowedAttributes.Add("disabled");

        if (!allowImages)
        {
            sanitizer.AllowedTags.Remove("img");
        }

        return sanitizer.Sanitize(rendered);
    }
}
