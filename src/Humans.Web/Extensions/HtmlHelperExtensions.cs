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

    public static IHtmlContent SanitizedMarkdown(this IHtmlHelper html, string? markdown)
    {
        ArgumentNullException.ThrowIfNull(html);

        if (string.IsNullOrWhiteSpace(markdown))
        {
            return HtmlString.Empty;
        }

        var pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();
        var rendered = Markdown.ToHtml(markdown, pipeline);
        var sanitized = new HtmlSanitizer().Sanitize(rendered);
        return new HtmlString(sanitized);
    }
}
