using System.IO;
using System.Text.Encodings.Web;
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
}
