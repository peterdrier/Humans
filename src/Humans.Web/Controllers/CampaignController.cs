using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Admin/Campaigns")]
public class CampaignController : HumansControllerBase
{
    private readonly ICampaignService _campaignService;
    private readonly ITicketVendorService _vendorService;

    public CampaignController(
        ICampaignService campaignService,
        ITicketVendorService vendorService,
        UserManager<User> userManager)
        : base(userManager)
    {
        _campaignService = campaignService;
        _vendorService = vendorService;
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
        if (string.IsNullOrWhiteSpace(title))
            ModelState.AddModelError(nameof(title), "Title is required.");
        if (string.IsNullOrWhiteSpace(emailSubject))
            ModelState.AddModelError(nameof(emailSubject), "Email subject is required.");
        if (string.IsNullOrWhiteSpace(emailBodyTemplate))
            ModelState.AddModelError(nameof(emailBodyTemplate), "Email body template is required.");

        if (!ModelState.IsValid)
        {
            ViewBag.Title2 = title;
            ViewBag.Description = description;
            ViewBag.EmailSubject = emailSubject;
            ViewBag.EmailBodyTemplate = emailBodyTemplate;
            ViewBag.ReplyToAddress = replyToAddress;
            return View();
        }

        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null) return Unauthorized();

        var campaign = await _campaignService.CreateAsync(title, description, emailSubject, emailBodyTemplate, replyToAddress, currentUser.Id);
        SetSuccess("Campaign created.");
        return RedirectToAction(nameof(Detail), new { id = campaign.Id });
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
        if (string.IsNullOrWhiteSpace(title))
            ModelState.AddModelError(nameof(title), "Title is required.");
        if (string.IsNullOrWhiteSpace(emailSubject))
            ModelState.AddModelError(nameof(emailSubject), "Email subject is required.");
        if (string.IsNullOrWhiteSpace(emailBodyTemplate))
            ModelState.AddModelError(nameof(emailBodyTemplate), "Email body template is required.");

        if (!ModelState.IsValid)
        {
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

        var updated = await _campaignService.UpdateAsync(
            id,
            title,
            description,
            emailSubject,
            emailBodyTemplate,
            replyToAddress);
        if (!updated)
        {
            return NotFound();
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
    public async Task<IActionResult> ImportCodes(Guid id, IFormFile file)
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
        var campaign = await _campaignService.GetByIdAsync(id);
        if (campaign is null) return NotFound();

        if (campaign.Status != CampaignStatus.Draft)
        {
            SetError("Codes can only be generated for Draft campaigns.");
            return RedirectToAction(nameof(Detail), new { id });
        }

        if (count <= 0)
        {
            SetError("Count must be greater than zero.");
            return RedirectToAction(nameof(Detail), new { id });
        }

        if (!Enum.TryParse<DiscountType>(discountType, ignoreCase: true, out var parsedType))
        {
            SetError("Invalid discount type.");
            return RedirectToAction(nameof(Detail), new { id });
        }

        var spec = new DiscountCodeSpec(count, parsedType, discountValue, ExpiresAt: null);
        var codes = await _vendorService.GenerateDiscountCodesAsync(spec);
        await _campaignService.ImportGeneratedCodesAsync(id, codes);

        SetSuccess($"Generated and imported {codes.Count} discount codes.");
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
