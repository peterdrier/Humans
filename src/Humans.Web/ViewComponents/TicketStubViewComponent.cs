using Humans.Application.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

/// <summary>
/// Renders one held ticket as a physical admission stub. A pending outgoing
/// transfer shows a "transfer pending" stamp; voided tickets render muted.
/// Shared by the transfer wizard, the /Profile/Me ticket card, and the homepage.
/// </summary>
public sealed class TicketStubViewComponent : ViewComponent
{
    public IViewComponentResult Invoke(TicketStubInfo stub) => View("Default", stub);
}
