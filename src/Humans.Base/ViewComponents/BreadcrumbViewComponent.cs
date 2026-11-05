using System.Text.Encodings.Web;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Base.ViewComponents;

/// <summary>The member layout's breadcrumb items, in order; the last is the current page.</summary>
public sealed record BreadcrumbViewModel(IReadOnlyList<IHtmlContent> Items);

/// <summary>
/// The member layout's Bootstrap breadcrumb, built from the page's <c>Crumbs</c> section — the same
/// section <c>_AdminLayout</c> hands to <see cref="AdminBreadcrumbViewComponent"/>, so a page writes
/// its crumbs once, for either layout. Each top-level element or text run of the section is one item
/// (links first, the current page last); an attribute-less <c>&lt;span&gt;</c> contributes its content.
/// </summary>
public sealed class BreadcrumbViewComponent : ViewComponent
{
    private static readonly HtmlParser Parser = new();

    public IViewComponentResult Invoke(IHtmlContent? trail) => View(new BreadcrumbViewModel(Items(trail)));

    private static List<IHtmlContent> Items(IHtmlContent? trail)
    {
        var items = new List<IHtmlContent>();
        if (trail is null)
            return items;

        using var writer = new StringWriter();
        trail.WriteTo(writer, HtmlEncoder.Default);
        var body = Parser.ParseDocument(writer.ToString()).Body;
        if (body is null)
            return items;

        foreach (var node in body.ChildNodes)
        {
            if (node is IElement { LocalName: "span", Attributes.Length: 0 } bare)
                items.Add(new HtmlString(bare.InnerHtml));
            else if (node is IElement || (node is IText text && !string.IsNullOrWhiteSpace(text.Data)))
                items.Add(new HtmlString(node.ToHtml()));
        }

        return items;
    }
}
