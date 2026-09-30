using AngleSharp.Html.Parser;

namespace Humans.Base.Helpers;

public static class HtmlPlainTextConverter
{
    public static string Convert(string html)
    {
        // Reuse the HTML parser already carried by Base's sanitizer. Regex tag
        // stripping loses action URLs and mistakes quoted '>' characters for tags.
        using var document = new HtmlParser().ParseDocument(html);
        foreach (var link in document.QuerySelectorAll("a[href]"))
        {
            var destination = link.GetAttribute("href");
            if (!string.IsNullOrEmpty(destination)
                && !string.Equals(link.TextContent, destination, StringComparison.Ordinal))
                link.AppendChild(document.CreateTextNode($" ({destination})"));
        }

        foreach (var lineBreak in document.QuerySelectorAll("br"))
            lineBreak.Parent?.InsertBefore(document.CreateTextNode("\n"), lineBreak);

        foreach (var block in document.QuerySelectorAll("p,h1,h2,h3,h4,h5,h6,li"))
            block.AppendChild(document.CreateTextNode(block.LocalName is "li" ? "\n" : "\n\n"));

        // DOM text has already decoded entities once. Decoding again would turn
        // literal '&lt;text&gt;' copy into markup characters.
        return document.Body?.TextContent.Trim() ?? string.Empty;
    }
}
