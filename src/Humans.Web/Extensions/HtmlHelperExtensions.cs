using System.IO;
using System.Text.Encodings.Web;
using Ganss.Xss;
using Markdig;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Humans.Web.Extensions;

public static class HtmlHelperExtensions
{
    public static string AntiForgeryTokenHtmlForJavaScript(this IHtmlHelper html)
    {
        ArgumentNullException.ThrowIfNull(html);

        using var writer = new StringWriter();
        html.AntiForgeryToken().WriteTo(writer, HtmlEncoder.Default);
        return writer.ToString().Replace("'", "\\'", StringComparison.Ordinal);
    }

    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static IHtmlContent SanitizedMarkdown(this IHtmlHelper html, string? markdown)
    {
        ArgumentNullException.ThrowIfNull(html);

        if (string.IsNullOrWhiteSpace(markdown))
        {
            return HtmlString.Empty;
        }

        var rendered = Markdown.ToHtml(markdown, MarkdownPipeline);
        var sanitizer = new HtmlSanitizer();

        // Allow task list checkboxes rendered by Markdig's UseTaskLists extension
        sanitizer.AllowedTags.Add("input");
        sanitizer.AllowedAttributes.Add("type");
        sanitizer.AllowedAttributes.Add("checked");
        sanitizer.AllowedAttributes.Add("disabled");

        var sanitized = sanitizer.Sanitize(rendered);
        return new HtmlString(sanitized);
    }
}
