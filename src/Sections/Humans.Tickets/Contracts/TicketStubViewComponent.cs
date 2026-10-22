using Microsoft.AspNetCore.Mvc;

namespace Humans.Tickets.Contracts;

/// <summary>Public because Scanner's ticket card renders it, and a section's views reach only assemblies it references.</summary>
public sealed class TicketStubViewComponent : ViewComponent
{
    public IViewComponentResult Invoke(TicketStubInfo stub) => View("Default", stub);
}
