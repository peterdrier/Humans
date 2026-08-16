using Humans.Application.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Tickets.Contracts;

/// <summary>
/// Renders one held ticket as a physical admission stub. A pending outgoing
/// transfer shows a "transfer pending" stamp; voided tickets render muted.
/// Shared by the transfer wizard, the /Profile/Me ticket card, and the homepage.
/// </summary>
/// <remarks>
/// On the Tickets contracts leaf rather than Shell because <c>Humans.Scanner</c>'s ticket card
/// renders <c>&lt;vc:ticket-stub&gt;</c>, and a section's <c>_ViewImports</c> can only reach
/// assemblies it references — a Shell-resident component renders as inert literal
/// markup from a section view (G5-SECTION-TEMPLATE.md step 6). It qualifies: the component
/// names no section vocabulary, only <c>TicketStubInfo</c>, which already lives in Base.
/// </remarks>
public sealed class TicketStubViewComponent : ViewComponent
{
    public IViewComponentResult Invoke(TicketStubInfo stub) => View("Default", stub);
}
