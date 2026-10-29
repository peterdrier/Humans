using Microsoft.AspNetCore.Mvc;

namespace Humans.Agent.ViewComponents;

/// <summary>The widget gallery's Agent card: a description only, since the help launcher is already in the page chrome.</summary>
internal sealed class AgentGalleryViewComponent : ViewComponent
{
    public IViewComponentResult Invoke() => View();
}
