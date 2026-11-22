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

    /// <summary>Renders Markdown, applies optional HTML composition, then sanitizes the complete result.</summary>
    public static string Render(string? markdown, bool allowImages = true, Func<string, string>? transformHtml = null)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var rendered = Markdown.ToHtml(markdown, MarkdownPipeline);
        if (transformHtml is not null)
            rendered = transformHtml(rendered);
        var sanitizer = new HtmlSanitizer();

        // Allow task list checkboxes rendered by Markdig's UseTaskLists extension.
        sanitizer.AllowedTags.Add("input");
        sanitizer.AllowedAttributes.Add("type");
        sanitizer.AllowedAttributes.Add("checked");
        sanitizer.AllowedAttributes.Add("disabled");
        sanitizer.PostProcessDom += (_, e) =>
        {
            foreach (var input in e.Document.QuerySelectorAll("input"))
            {
                if (!string.Equals(input.GetAttribute("type"), "checkbox", StringComparison.OrdinalIgnoreCase)
                    || !input.HasAttribute("disabled"))
                    input.Remove();
            }
        };

        // User Markdown never needs inline CSS, and a style attribute's url(...) (e.g.
        // background-image) is not covered by the https-only <img> rule below — it would be a
        // tracking-pixel path the https-only rule doesn't close. Disallow style outright.
        sanitizer.AllowedAttributes.Remove("style");

        if (!allowImages)
        {
            sanitizer.AllowedTags.Remove("img");
        }
        else
        {
            // Image sources require HTTPS; links retain the sanitizer's broader URL allowance.
            sanitizer.FilterUrl += (_, e) =>
            {
                if (!string.Equals(e.Tag.TagName, "IMG", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (e.SanitizedUrl is null
                    || !e.SanitizedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    e.SanitizedUrl = null;
                }
            };
        }

        return sanitizer.Sanitize(rendered);
    }
}
