using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.Constants;
using Humans.Application.Interfaces;
using Humans.Application.Services;
using Humans.Web.Authorization;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Route("Guide")]
public class GuideController : Controller
{
    private readonly IGuideContentService _content;
    private readonly IGuideRoleResolver _roles;

    public GuideController(IGuideContentService content, IGuideRoleResolver roles)
    {
        _content = content;
        _roles = roles;
    }

    [HttpGet("")]
    [AllowAnonymous]
    public Task<IActionResult> Index(CancellationToken cancellationToken) =>
        RenderAsync(GuideFiles.Readme, cancellationToken);

    [HttpGet("{name}")]
    [AllowAnonymous]
    public Task<IActionResult> Document(string name, CancellationToken cancellationToken) =>
        RenderAsync(name, cancellationToken);

    [HttpPost("Refresh")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Refresh(CancellationToken cancellationToken)
    {
        try
        {
            await _content.RefreshAllAsync(cancellationToken);
            TempData["GuideRefreshStatus"] = "Guide refreshed from GitHub.";
        }
        catch (GuideContentUnavailableException ex)
        {
            TempData["GuideRefreshStatus"] = $"Refresh failed: {ex.Message}";
        }
        return RedirectToAction(nameof(Index));
    }

    private async Task<IActionResult> RenderAsync(string requestedStem, CancellationToken cancellationToken)
    {
        var canonical = GuideFiles.All.FirstOrDefault(s =>
            s.Equals(requestedStem, StringComparison.OrdinalIgnoreCase));

        if (canonical is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return View("NotFound", BuildSidebar(null));
        }

        string rendered;
        try
        {
            rendered = await _content.GetRenderedAsync(canonical, cancellationToken);
        }
        catch (GuideContentUnavailableException)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return View("Unavailable", BuildSidebar(canonical));
        }

        var roleContext = await _roles.ResolveAsync(User, cancellationToken);
        var filtered = GuideFilter.Apply(rendered, roleContext);

        var viewModel = new GuideViewModel
        {
            Title = DisplayName(canonical),
            Html = new HtmlString(filtered),
            Sidebar = BuildSidebar(canonical),
            FileStem = canonical
        };

        ViewData["Title"] = viewModel.Title;
        return View(canonical.Equals(GuideFiles.Readme, StringComparison.OrdinalIgnoreCase)
            ? "Index"
            : "Document", viewModel);
    }

    private static GuideSidebarModel BuildSidebar(string? activeStem)
    {
        var entries = new List<GuideSidebarEntry>
        {
            new(GuideFiles.GettingStarted, "Getting Started", "Start here")
        };
        foreach (var section in GuideFiles.Sections)
        {
            entries.Add(new GuideSidebarEntry(section, DisplayName(section), "Section guides"));
        }
        entries.Add(new GuideSidebarEntry(GuideFiles.Glossary, "Glossary", "Appendix"));
        return new GuideSidebarModel { Entries = entries, ActiveStem = activeStem };
    }

    private static string DisplayName(string stem) => stem switch
    {
        GuideFiles.Readme => "Guide",
        GuideFiles.GettingStarted => "Getting Started",
        GuideFiles.Glossary => "Glossary",
        "LegalAndConsent" => "Legal & Consent",
        "CityPlanning" => "City Planning",
        "GoogleIntegration" => "Google Integration",
        _ => stem
    };
}
