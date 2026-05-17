using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Domain.Entities;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Humans.Application.Interfaces.Campaigns;

using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Admin/Campaigns")]
public class CampaignController : HumansControllerBase
{
    private readonly ICampaignService _campaignService;

    public CampaignController(
        ICampaignService campaignService,
        IUserService userService)
        : base(userService)
    {
        _campaignService = campaignService;
    }

    [HttpGet("")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Index()
    {
        var campaigns = await _campaignService.GetAllAsync();
        return View(campaigns);
    }

    [HttpGet("Create")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public IActionResult Create()
    {
        return View();
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Create(string title, string? description, string emailSubject, string emailBodyTemplate, string? replyToAddress)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null) return Unauthorized();

        var result = await _campaignService.CreateAsync(
            title, description, emailSubject, emailBodyTemplate, replyToAddress, currentUser.Id);
        if (!result.Success)
        {
            if (string.Equals(result.ErrorKey, "TitleRequired", StringComparison.Ordinal))
                ModelState.AddModelError(nameof(title), "Title is required.");
            else if (string.Equals(result.ErrorKey, "EmailSubjectRequired", StringComparison.Ordinal))
                ModelState.AddModelError(nameof(emailSubject), "Email subject is required.");
            else if (string.Equals(result.ErrorKey, "EmailBodyTemplateRequired", StringComparison.Ordinal))
                ModelState.AddModelError(nameof(emailBodyTemplate), "Email body template is required.");

            ViewBag.Title2 = title;
            ViewBag.Description = description;
            ViewBag.EmailSubject = emailSubject;
            ViewBag.EmailBodyTemplate = emailBodyTemplate;
            ViewBag.ReplyToAddress = replyToAddress;
            return View();
        }

        SetSuccess("Campaign created.");
        return RedirectToAction(nameof(Detail), new { id = result.Campaign!.Id });
    }

    [HttpGet("Edit/{id:guid}")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Edit(Guid id)
    {
        var campaign = await _campaignService.GetByIdAsync(id);
        if (campaign is null) return NotFound();
        return View(campaign);
    }

    [HttpPost("Edit/{id:guid}")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Edit(Guid id, string title, string? description, string emailSubject, string emailBodyTemplate, string? replyToAddress)
    {
        var updated = await _campaignService.UpdateAsync(
            id,
            title,
            description,
            emailSubject,
            emailBodyTemplate,
            replyToAddress);
        if (string.Equals(updated.ErrorKey, "NotFound", StringComparison.Ordinal))
            return NotFound();

        if (!updated.Success)
        {
            if (string.Equals(updated.ErrorKey, "TitleRequired", StringComparison.Ordinal))
                ModelState.AddModelError(nameof(title), "Title is required.");
            else if (string.Equals(updated.ErrorKey, "EmailSubjectRequired", StringComparison.Ordinal))
                ModelState.AddModelError(nameof(emailSubject), "Email subject is required.");
            else if (string.Equals(updated.ErrorKey, "EmailBodyTemplateRequired", StringComparison.Ordinal))
                ModelState.AddModelError(nameof(emailBodyTemplate), "Email body template is required.");

            var campaign = await _campaignService.GetByIdAsync(id);
            if (campaign is null)
            {
                return NotFound();
            }

            // Pass submitted form values back via ViewBag for re-display
            ViewBag.Title2 = title;
            ViewBag.Description = description;
            ViewBag.EmailSubject = emailSubject;
            ViewBag.EmailBodyTemplate = emailBodyTemplate;
            ViewBag.ReplyToAddress = replyToAddress;
            return View(campaign);
        }

        SetSuccess("Campaign updated.");
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = PolicyNames.TicketAdminOrAdmin)]
    public async Task<IActionResult> Detail(Guid id)
    {
        var page = await _campaignService.GetDetailPageAsync(id);
        if (page is null) return NotFound();

        return View(new CampaignDetailViewModel
        {
            Campaign = page.Campaign,
            Stats = page.Stats
        });
    }

    [HttpPost("{id:guid}/ImportCodes")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> ImportCodes(Guid id, IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            SetError("Please select a CSV file.");
            return RedirectToAction(nameof(Detail), new { id });
        }

        using var reader = new StreamReader(file.OpenReadStream());
        var content = await reader.ReadToEndAsync();
        var codes = content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (codes.Count == 0)
        {
            SetError("No codes found in the file.");
            return RedirectToAction(nameof(Detail), new { id });
        }

        await _campaignService.ImportCodesAsync(id, codes);
        SetSuccess($"Imported {codes.Count} codes.");
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("{id:guid}/GenerateCodes")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.TicketAdminOrAdmin)]
    public async Task<IActionResult> GenerateCodes(Guid id, int count, string discountType, decimal discountValue)
    {
        var result = await _campaignService.GenerateAndImportDiscountCodesAsync(
            id, count, discountType, discountValue);
        if (string.Equals(result.ErrorKey, "NotFound", StringComparison.Ordinal))
            return NotFound();

        if (string.Equals(result.ErrorKey, "NotDraft", StringComparison.Ordinal))
            SetError("Codes can only be generated for Draft campaigns.");
        else if (string.Equals(result.ErrorKey, "InvalidCount", StringComparison.Ordinal))
            SetError("Count must be greater than zero.");
        else if (string.Equals(result.ErrorKey, "InvalidDiscountType", StringComparison.Ordinal))
            SetError("Invalid discount type.");
        else
            SetSuccess($"Generated and imported {result.GeneratedCount} discount codes.");

        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("{id:guid}/Activate")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Activate(Guid id)
    {
        await _campaignService.ActivateAsync(id);
        SetSuccess("Campaign activated.");
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("{id:guid}/Complete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Complete(Guid id)
    {
        await _campaignService.CompleteAsync(id);
        SetSuccess("Campaign completed.");
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpGet("{id:guid}/SendWave")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> SendWave(Guid id, Guid? teamId)
    {
        var page = await _campaignService.GetSendWavePageAsync(id, teamId);
        if (page is null) return NotFound();

        return View(new CampaignSendWaveViewModel
        {
            Campaign = page.Campaign,
            Teams = page.Teams,
            SelectedTeamId = page.SelectedTeamId,
            Preview = page.Preview
        });
    }

    [HttpPost("{id:guid}/SendWave")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> SendWave(Guid id, Guid teamId)
    {
        var sentCount = await _campaignService.SendWaveAsync(id, teamId);
        SetSuccess($"Wave sent to {sentCount} humans.");
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("Grants/{grantId:guid}/Resend")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Resend(Guid grantId)
    {
        var campaignId = await _campaignService.GetCampaignIdForGrantAsync(grantId);
        if (!campaignId.HasValue) return NotFound();

        await _campaignService.ResendToGrantAsync(grantId);
        SetSuccess("Resend queued.");
        return RedirectToAction(nameof(Detail), new { id = campaignId.Value });
    }

    [HttpPost("{id:guid}/RetryAllFailed")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> RetryAllFailed(Guid id)
    {
        await _campaignService.RetryAllFailedAsync(id);
        SetSuccess("Retrying all failed sends.");
        return RedirectToAction(nameof(Detail), new { id });
    }
}
