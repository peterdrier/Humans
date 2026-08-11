using Humans.Application.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace Humans.UI.ViewComponents;

/// <summary>
/// Renders one held ticket as a physical admission stub. A pending outgoing
/// transfer shows a "transfer pending" stamp; voided tickets render muted.
/// Shared by the transfer wizard, the /Profile/Me ticket card, and the homepage.
/// </summary>
/// <remarks>
/// In <c>Humans.UI</c> rather than Shell because <c>Humans.Scanner</c>'s ticket card renders
/// <c>&lt;vc:ticket-stub&gt;</c>, and a section's <c>_ViewImports</c> can only
/// <c>@addTagHelper *, Humans.UI</c> — a Shell-resident component renders as inert literal
/// markup from a section view (G5-SECTION-TEMPLATE.md step 6). It qualifies: the component
/// names no section vocabulary, only <c>TicketStubInfo</c>, which already lives in Base.
/// </remarks>
public sealed class TicketStubViewComponent : ViewComponent
{
    public IViewComponentResult Invoke(TicketStubInfo stub) => View("Default", stub);
}
