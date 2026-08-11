using Microsoft.AspNetCore.Html;

namespace Humans.Guide.Models;

internal sealed class GuideViewModel
{
    public required string Title { get; init; }
    public required HtmlString Html { get; init; }
    public required GuideSidebarModel Sidebar { get; init; }
    public required string? FileStem { get; init; }
}
