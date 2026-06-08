using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Humans.Integration.Tests.Localization;

/// <summary>
/// Extracts user-visible text runs from rendered HTML and tests them for the pseudo-localizer
/// marker. Script/style/etc. containers are skipped, whitespace is collapsed, and runs with no
/// letters (pure digits/punctuation) are dropped — they are never localizable prose.
/// </summary>
internal static class RenderedTextScanner
{
    private static readonly HtmlParser Parser = new();

    private static readonly HashSet<string> SkipContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        "SCRIPT", "STYLE", "NOSCRIPT", "TEMPLATE", "HEAD", "SVG",
    };

    /// <summary>
    /// One run per visible text node — enough granularity to test each for the marker. Scanning
    /// is scoped to the page's <c>&lt;main&gt;</c> content region so the shared layout shell
    /// (nav, sidebar, language switcher, footer, feedback modal) — which is identical on every
    /// page and is its own concern — does not drown the per-page signal. Falls back to the whole
    /// body if a page has no <c>&lt;main&gt;</c>.
    /// </summary>
    public static IReadOnlyList<string> ExtractTextRuns(string html)
    {
        var document = Parser.ParseDocument(html);
        var root = document.QuerySelector("main") ?? (IElement?)document.Body;
        if (root is null)
            return [];

        var runs = new List<string>();
        var stack = new Stack<INode>();
        PushChildren(stack, root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            switch (node)
            {
                case IText text:
                    var run = CollapseWhitespace(text.Data);
                    if (run.Length > 0 && ContainsLetter(run))
                        runs.Add(run);
                    break;

                case IElement element when SkipContainers.Contains(element.TagName):
                    break; // don't descend into script/style/etc.

                default:
                    PushChildren(stack, node);
                    break;
            }
        }

        return runs;
    }

    private static void PushChildren(Stack<INode> stack, INode node)
    {
        foreach (var child in node.ChildNodes)
            stack.Push(child);
    }

    public static bool HasMarker(string run) =>
        run.Contains(PseudoStringLocalizer.Open) || run.Contains(PseudoStringLocalizer.Close);

    public static string StripMarkers(string run) =>
        run.Replace(PseudoStringLocalizer.Open.ToString(), string.Empty)
           .Replace(PseudoStringLocalizer.Close.ToString(), string.Empty);

    private static bool ContainsLetter(string value)
    {
        foreach (var c in value)
        {
            if (char.IsLetter(c))
                return true;
        }

        return false;
    }

    private static string CollapseWhitespace(string value)
    {
        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
                sb.Append(' ');
            pendingSpace = false;
            sb.Append(c);
        }

        return sb.ToString();
    }
}
