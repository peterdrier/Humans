using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Humans.Guide.Models;
using Humans.Guide.Services;
using Humans.UI.Authorization;

namespace Humans.Guide.Controllers;

[Route("Guide")]
internal sealed class GuideController(IGuideContentService content, IGuideRoleResolver roles) : Controller
{
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
            await content.RefreshAllAsync(cancellationToken);
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
        if (!GuideFiles.TryCanonical(requestedStem, out var canonical))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return View("NotFound");
        }

        string rendered;
        try
        {
            rendered = await content.GetRenderedAsync(canonical, cancellationToken);
        }
        catch (GuideContentUnavailableException)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return View("Unavailable");
        }

        var roleContext = await roles.ResolveAsync(User, cancellationToken);
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
        foreach (var faq in GuideFiles.CommonQuestions)
        {
            entries.Add(new GuideSidebarEntry(faq, DisplayName(faq), "Common questions"));
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
        "EmailAccount" => "Your @nobodies.team email",
        "TwoStepVerification" => "Two-step verification (2FA)",
        "TicketTransfers" => "Transferring your ticket",
        "AiHelper" => "The in-app AI helper",
        "SigningIn" => "Signing in & getting unstuck",
        "YourData" => "Your data & privacy",
        _ => stem
    };
}
