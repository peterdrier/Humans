using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleGroups.HumanAdminBoardOrAdmin)]
[Route("Contacts")]
public class ContactsController : HumansControllerBase
{
    private readonly IContactService _contactService;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly ILogger<ContactsController> _logger;

    public ContactsController(
        UserManager<User> userManager,
        IContactService contactService,
        IStringLocalizer<SharedResource> localizer,
        ILogger<ContactsController> logger)
        : base(userManager)
    {
        _contactService = contactService;
        _localizer = localizer;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string? search)
    {
        try
        {
            var allRows = await _contactService.GetFilteredContactsAsync(search);

            var viewModel = new AdminContactListViewModel
            {
                TotalCount = allRows.Count,
                SearchTerm = search,
                Contacts = allRows.Select(r => new AdminContactViewModel
                {
                    Id = r.UserId,
                    Email = r.Email,
                    DisplayName = r.DisplayName,
                    ContactSource = r.ContactSource,
                    ExternalSourceId = r.ExternalSourceId,
                    CreatedAt = r.CreatedAt,
                    HasCommunicationPreferences = r.HasCommunicationPreferences
                }).ToList()
            };

            return View(viewModel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading contacts list");
            SetError(_localizer["Common_Error"].Value);
            return RedirectToAction(nameof(ProfileController.AdminList), "Profile");
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id)
    {
        try
        {
            var contact = await _contactService.GetContactDetailAsync(id);
            if (contact is null)
                return NotFound();

            var viewModel = new AdminContactDetailViewModel
            {
                UserId = contact.Id,
                Email = contact.Email ?? string.Empty,
                DisplayName = contact.DisplayName,
                ContactSource = contact.ContactSource,
                ExternalSourceId = contact.ExternalSourceId,
                CreatedAt = contact.CreatedAt,
                CommunicationPreferences = contact.CommunicationPreferences.ToList()
            };

            return View(viewModel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading contact detail for {ContactId}", id);
            SetError(_localizer["Common_Error"].Value);
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpGet("Create")]
    public IActionResult Create()
    {
        return View(new CreateContactViewModel());
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateContactViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        try
        {
            var currentUser = await GetCurrentUserAsync();
            if (currentUser is null)
                return Unauthorized();

            var contact = await _contactService.CreateContactAsync(
                model.Email, model.DisplayName, model.Source);

            SetSuccess($"Contact created for {model.Email}.");
            return RedirectToAction(nameof(Detail), new { id = contact.Id });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to create contact for {Email}", model.Email);
            ModelState.AddModelError(string.Empty, ex.Message);
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating contact for {Email}", model.Email);
            ModelState.AddModelError(string.Empty, _localizer["Common_Error"].Value);
            return View(model);
        }
    }
}
