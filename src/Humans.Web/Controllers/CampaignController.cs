using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Web.Controllers;

[Authorize(Roles = "Admin")]
[Route("Admin/Campaigns")]
public class CampaignController : Controller
{
    private readonly ICampaignService _campaignService;
    private readonly ITicketVendorService _vendorService;
    private readonly HumansDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly ILogger<CampaignController> _logger;

    public CampaignController(
        ICampaignService campaignService,
        ITicketVendorService vendorService,
        HumansDbContext dbContext,
        UserManager<User> userManager,
        ILogger<CampaignController> logger)
    {
        _campaignService = campaignService;
        _vendorService = vendorService;
        _dbContext = dbContext;
        _userManager = userManager;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var campaigns = await _campaignService.GetAllAsync();
        return View(campaigns);
    }

    [HttpGet("Create")]
    public IActionResult Create()
    {
        return View();
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
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

        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null) return Unauthorized();

        var campaign = await _campaignService.CreateAsync(title, description, emailSubject, emailBodyTemplate, replyToAddress, currentUser.Id);
        TempData["SuccessMessage"] = "Campaign created.";
        return RedirectToAction(nameof(Detail), new { id = campaign.Id });
    }

    [HttpGet("Edit/{id:guid}")]
    public async Task<IActionResult> Edit(Guid id)
    {
        var campaign = await _dbContext.Set<Campaign>().FindAsync(id);
        if (campaign == null) return NotFound();
        return View(campaign);
    }

    [HttpPost("Edit/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, string title, string? description, string emailSubject, string emailBodyTemplate, string? replyToAddress)
    {
        if (string.IsNullOrWhiteSpace(title))
            ModelState.AddModelError(nameof(title), "Title is required.");
        if (string.IsNullOrWhiteSpace(emailSubject))
            ModelState.AddModelError(nameof(emailSubject), "Email subject is required.");
        if (string.IsNullOrWhiteSpace(emailBodyTemplate))
            ModelState.AddModelError(nameof(emailBodyTemplate), "Email body template is required.");

        var campaign = await _dbContext.Set<Campaign>().FindAsync(id);
        if (campaign == null) return NotFound();

        if (!ModelState.IsValid)
            return View(campaign);

        campaign.Title = title.Trim();
        campaign.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        campaign.EmailSubject = emailSubject.Trim();
        campaign.EmailBodyTemplate = emailBodyTemplate.Trim();
        campaign.ReplyToAddress = string.IsNullOrWhiteSpace(replyToAddress) ? null : replyToAddress.Trim();

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Campaign {CampaignId} updated by {User}", id, User.Identity?.Name);
        TempData["SuccessMessage"] = "Campaign updated.";
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id)
    {
        var campaign = await _campaignService.GetByIdAsync(id);
        if (campaign == null) return NotFound();

        var totalCodes = campaign.Codes.Count;
        var assignedCodeIds = campaign.Grants.Select(g => g.CampaignCodeId).ToHashSet();
        var availableCodes = totalCodes - assignedCodeIds.Count;
        var sentCount = campaign.Grants.Count(g => g.LatestEmailStatus == EmailOutboxStatus.Sent);
        var failedCount = campaign.Grants.Count(g => g.LatestEmailStatus == EmailOutboxStatus.Failed);

        var codesRedeemed = campaign.Grants.Count(g => g.RedeemedAt != null);
        var totalGrants = campaign.Grants.Count;

        ViewBag.TotalCodes = totalCodes;
        ViewBag.AvailableCodes = availableCodes;
        ViewBag.SentCount = sentCount;
        ViewBag.FailedCount = failedCount;
        ViewBag.CodesRedeemed = codesRedeemed;
        ViewBag.TotalGrants = totalGrants;

        return View(campaign);
    }

    [HttpPost("{id:guid}/ImportCodes")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportCodes(Guid id, IFormFile file)
    {
        if (file == null || file.Length == 0)
        {
            TempData["ErrorMessage"] = "Please select a CSV file.";
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
            TempData["ErrorMessage"] = "No codes found in the file.";
            return RedirectToAction(nameof(Detail), new { id });
        }

        await _campaignService.ImportCodesAsync(id, codes);
        TempData["SuccessMessage"] = $"Imported {codes.Count} codes.";
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("{id:guid}/GenerateCodes")]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = $"{RoleNames.TicketAdmin},{RoleNames.Admin}")]
    public async Task<IActionResult> GenerateCodes(Guid id, int count, string discountType, decimal discountValue)
    {
        var campaign = await _dbContext.Set<Campaign>().FindAsync(id);
        if (campaign == null) return NotFound();

        if (campaign.Status != CampaignStatus.Draft)
        {
            TempData["ErrorMessage"] = "Codes can only be generated for Draft campaigns.";
            return RedirectToAction(nameof(Detail), new { id });
        }

        if (count <= 0)
        {
            TempData["ErrorMessage"] = "Count must be greater than zero.";
            return RedirectToAction(nameof(Detail), new { id });
        }

        if (!Enum.TryParse<DiscountType>(discountType, out var parsedType))
        {
            TempData["ErrorMessage"] = "Invalid discount type.";
            return RedirectToAction(nameof(Detail), new { id });
        }

        var spec = new DiscountCodeSpec(count, parsedType, discountValue, ExpiresAt: null);
        var codes = await _vendorService.GenerateDiscountCodesAsync(spec);
        await _campaignService.ImportGeneratedCodesAsync(id, codes);

        TempData["SuccessMessage"] = $"Generated and imported {codes.Count} discount codes.";
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("{id:guid}/Activate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Activate(Guid id)
    {
        await _campaignService.ActivateAsync(id);
        TempData["SuccessMessage"] = "Campaign activated.";
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("{id:guid}/Complete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Complete(Guid id)
    {
        await _campaignService.CompleteAsync(id);
        TempData["SuccessMessage"] = "Campaign completed.";
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpGet("{id:guid}/SendWave")]
    public async Task<IActionResult> SendWave(Guid id, Guid? teamId)
    {
        var campaign = await _campaignService.GetByIdAsync(id);
        if (campaign == null) return NotFound();

        var teams = await _dbContext.Teams.OrderBy(t => t.Name).ToListAsync();
        ViewBag.Teams = teams;
        ViewBag.Campaign = campaign;
        ViewBag.SelectedTeamId = teamId;

        if (teamId.HasValue)
        {
            var preview = await _campaignService.PreviewWaveSendAsync(id, teamId.Value);
            ViewBag.Preview = preview;
        }

        return View();
    }

    [HttpPost("{id:guid}/SendWave")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendWave(Guid id, Guid teamId)
    {
        var sentCount = await _campaignService.SendWaveAsync(id, teamId);
        TempData["SuccessMessage"] = $"Wave sent to {sentCount} humans.";
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("Grants/{grantId:guid}/Resend")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resend(Guid grantId)
    {
        // Need to find the campaign ID for redirect
        var grant = await _dbContext.Set<CampaignGrant>().FindAsync(grantId);
        if (grant == null) return NotFound();

        await _campaignService.ResendToGrantAsync(grantId);
        TempData["SuccessMessage"] = "Resend queued.";
        return RedirectToAction(nameof(Detail), new { id = grant.CampaignId });
    }

    [HttpPost("{id:guid}/RetryAllFailed")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RetryAllFailed(Guid id)
    {
        await _campaignService.RetryAllFailedAsync(id);
        TempData["SuccessMessage"] = "Retrying all failed sends.";
        return RedirectToAction(nameof(Detail), new { id });
    }
}
