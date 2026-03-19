using System.Net;
using System.Text.RegularExpressions;

namespace Humans.Infrastructure.Helpers;

public static partial class HtmlPlainTextConverter
{
    public static string Convert(string html)
    {
        var text = html;
        text = LineBreakRegex().Replace(text, "\n");
        text = ParagraphEndRegex().Replace(text, "\n\n");
        text = ListItemEndRegex().Replace(text, "\n");
        text = HtmlTagRegex().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        return text.Trim();
    }

    [GeneratedRegex("<br\\s*/?>", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LineBreakRegex();

    [GeneratedRegex("</p>", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ParagraphEndRegex();

    [GeneratedRegex("</li>", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ListItemEndRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HtmlTagRegex();
}
