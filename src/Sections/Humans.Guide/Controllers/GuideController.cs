using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Humans.Guide.Models;
using Humans.Guide.Services;
using Humans.Base.Authorization;
using Humans.Base;
using Microsoft.Extensions.Localization;

namespace Humans.Guide.Controllers;

[Route("Guide")]
internal sealed class GuideController(
    IGuideContentService content,
    IGuideRoleResolver roles,
    IStringLocalizer<GuideResource> localizer,
    IStringLocalizer<SharedResource> sharedLocalizer) : Controller
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

        var roleContext = await roles.ResolveAsync(User, cancellationToken);

        string html;
        try
        {
            html = await content.GetPageAsync(canonical, roleContext, cancellationToken);
        }
        catch (GuideContentUnavailableException)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return View("Unavailable");
        }

        var viewModel = new GuideViewModel
        {
            Title = DisplayName(canonical),
            Html = new HtmlString(html),
            Sidebar = BuildSidebar(canonical),
            FileStem = canonical
        };

        ViewData["Title"] = viewModel.Title;
        return View(canonical.Equals(GuideFiles.Readme, StringComparison.OrdinalIgnoreCase)
            ? "Index"
            : "Document", viewModel);
    }

    private GuideSidebarModel BuildSidebar(string? activeStem)
    {
        var entries = new List<GuideSidebarEntry>
        {
            new(GuideFiles.GettingStarted, DisplayName(GuideFiles.GettingStarted), localizer["Guide_Group_Start"].Value)
        };
        foreach (var section in GuideFiles.Sections)
        {
            entries.Add(new GuideSidebarEntry(section, DisplayName(section), localizer["Guide_Group_Sections"].Value));
        }
        foreach (var faq in GuideFiles.CommonQuestions)
        {
            entries.Add(new GuideSidebarEntry(faq, DisplayName(faq), localizer["Guide_Group_Questions"].Value));
        }
        entries.Add(new GuideSidebarEntry(GuideFiles.Glossary, DisplayName(GuideFiles.Glossary), localizer["Guide_Group_Appendix"].Value));
        return new GuideSidebarModel { Entries = entries, ActiveStem = activeStem };
    }

    private static readonly IReadOnlyDictionary<string, string> SharedPageLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [GuideFiles.Readme] = "SectionHelp_Guide",
        ["Admin"] = "Nav_Admin",
        ["Teams"] = "LinkPreview_Teams_Title",
        ["Shifts"] = "LinkPreview_Shifts_Title",
        ["Tickets"] = "LinkPreview_Tickets_Title",
        ["Camps"] = "LinkPreview_Camps_Title",
        ["Events"] = "LinkPreview_Events_Title",
        ["Calendar"] = "LinkPreview_Calendar_Title",
        ["Issues"] = "LinkPreview_Issues_Title",
        ["Feedback"] = "LinkPreview_Feedback_Title",
        ["Governance"] = "LinkPreview_Governance_Title",
        ["Budget"] = "LinkPreview_Budget_Title",
        ["Expenses"] = "LinkPreview_Expenses_Title",
        ["Store"] = "LinkPreview_Store_Title",
        ["CityPlanning"] = "LinkPreview_CityPlanning_Title"
    };

    private string DisplayName(string stem) =>
        SharedPageLabels.TryGetValue(stem, out var sharedKey)
            ? sharedLocalizer[sharedKey].Value
            : localizer["Guide_Page_" + stem].Value;
}
