using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Humans.Base.Extensions;

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
        return new HtmlString(SanitizedMarkdownRenderer.Render(markdown));
    }
}
