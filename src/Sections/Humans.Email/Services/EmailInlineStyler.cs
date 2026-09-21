using AngleSharp.Html.Parser;

namespace Humans.Email.Services;

/// <summary>
/// Adds inline <c>style</c> attributes to the elements Markdown can produce, matching
/// <see cref="BrandedEmailTemplate"/>'s palette. Many email clients (Outlook, Gmail) strip
/// or ignore a <c>&lt;style&gt;</c> block, so headings, links, lists, quotes, tables, rules
/// and code blocks need their look baked into the tag itself.
/// </summary>
internal static class EmailInlineStyler
{
    private static readonly IReadOnlyDictionary<string, string> TagStyles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["h1"] = "font-family:'Cormorant Garamond',Georgia,'Times New Roman',serif;color:#3d2b1f;font-weight:600;margin:0 0 12px 0;",
        ["h2"] = "font-family:'Cormorant Garamond',Georgia,'Times New Roman',serif;color:#3d2b1f;font-weight:600;margin:0 0 12px 0;",
        ["h3"] = "font-family:'Cormorant Garamond',Georgia,'Times New Roman',serif;color:#3d2b1f;font-weight:600;margin:0 0 10px 0;",
        ["p"] = "margin:0 0 12px 0;color:#3d2b1f;line-height:1.6;",
        ["a"] = "color:#8b6914;",
        ["ul"] = "margin:0 0 12px 0;padding-left:20px;color:#3d2b1f;",
        ["ol"] = "margin:0 0 12px 0;padding-left:20px;color:#3d2b1f;",
        ["li"] = "margin:0 0 4px 0;",
        ["blockquote"] = "margin:0 0 12px 0;padding:8px 16px;border-left:3px solid #c9a96e;color:#6b5a4e;background:#f0e2c8;",
        ["table"] = "border-collapse:collapse;margin:0 0 12px 0;width:100%;",
        ["th"] = "border:1px solid #e8d4ab;padding:6px 10px;background:#f0e2c8;text-align:left;color:#3d2b1f;",
        ["td"] = "border:1px solid #e8d4ab;padding:6px 10px;color:#3d2b1f;",
        ["hr"] = "border:none;border-top:1px solid #e8d4ab;margin:16px 0;",
        ["code"] = "background:#f0e2c8;padding:2px 5px;border-radius:3px;font-family:Consolas,Monaco,monospace;font-size:0.9em;",
        ["pre"] = "background:#f0e2c8;padding:12px;border-radius:4px;overflow-x:auto;font-family:Consolas,Monaco,monospace;font-size:0.9em;",
    };

    /// <summary>
    /// Parses <paramref name="html"/> as a fragment and stamps a <c>style</c> attribute onto
    /// every element in <see cref="TagStyles"/>, prepending the palette to any style the element
    /// already carries so the element's own declarations still win. Returns the original string
    /// unchanged if it is empty or fails to parse.
    /// </summary>
    public static string Apply(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return html;
        }

        using var document = new HtmlParser().ParseDocument(html);

        if (document.Body is null)
        {
            return html;
        }

        foreach (var (tag, style) in TagStyles)
        {
            foreach (var element in document.Body.QuerySelectorAll(tag))
            {
                var existing = element.GetAttribute("style");
                // Palette first, the element's own style last: later declarations win in CSS, so a
                // template that already sets (say) a button's colour keeps it.
                element.SetAttribute("style", string.IsNullOrEmpty(existing) ? style : $"{style}{existing}");
            }
        }

        return document.Body.InnerHtml;
    }
}
