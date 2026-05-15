using Humans.Application.Interfaces.Events;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Web.Filters;
using Humans.Web.Models.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using static Humans.Web.Helpers.EventsTimeHelpers;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleGroups.EventsAdminOrAdmin)]
[Route("Events/Admin")]
[ServiceFilter(typeof(EventsFeatureFilter))]
public class EventsAdminController : HumansControllerBase
{
    private readonly IEventService _guide;
    private readonly ILogger<EventsAdminController> _logger;

    public EventsAdminController(
        IEventService guide,
        ILogger<EventsAdminController> logger,
        UserManager<User> userManager)
        : base(userManager)
    {
        _guide = guide;
        _logger = logger;
    }

    // ─── Settings ────────────────────────────────────────────

    [HttpGet("Settings")]
    public async Task<IActionResult> Settings()
    {
        var existing = await _guide.GetGuideSettingsAsync();
        var eventSettingsOptions = await BuildEventSettingsOptionsAsync();

        if (existing == null)
        {
            return View(new GuideSettingsViewModel
            {
                AvailableEventSettings = eventSettingsOptions,
                MaxPrintSlots = 100
            });
        }

        var eventSettings = await _guide.GetEventSettingsByIdAsync(existing.EventSettingsId);
        var tz = eventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventSettings.TimeZoneId)
            : null;
        return View(new GuideSettingsViewModel
        {
            Id = existing.Id,
            EventSettingsId = existing.EventSettingsId,
            SubmissionOpenAt = ToLocalDateTime(existing.SubmissionOpenAt, tz),
            SubmissionCloseAt = ToLocalDateTime(existing.SubmissionCloseAt, tz),
            GuidePublishAt = ToLocalDateTime(existing.GuidePublishAt, tz),
            MaxPrintSlots = existing.MaxPrintSlots,
            AvailableEventSettings = eventSettingsOptions,
            TimeZoneId = eventSettings?.TimeZoneId
        });
    }

    [HttpPost("Settings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSettings(GuideSettingsViewModel model)
    {
        if (!ModelState.IsValid)
        {
            model.AvailableEventSettings = await BuildEventSettingsOptionsAsync();
            return View(nameof(Settings), model);
        }

        var eventSettings = await _guide.GetEventSettingsByIdAsync(model.EventSettingsId);
        if (eventSettings == null)
        {
            ModelState.AddModelError(nameof(model.EventSettingsId), "Selected event edition not found.");
            model.AvailableEventSettings = await BuildEventSettingsOptionsAsync();
            return View(nameof(Settings), model);
        }

        try
        {
            await _guide.SaveGuideSettingsAsync(
                model.Id == Guid.Empty ? null : model.Id,
                model.EventSettingsId,
                LocalDateTime.FromDateTime(model.SubmissionOpenAt),
                LocalDateTime.FromDateTime(model.SubmissionCloseAt),
                LocalDateTime.FromDateTime(model.GuidePublishAt),
                model.MaxPrintSlots);

            _logger.LogInformation("Guide settings saved for event {EventSettingsId}", model.EventSettingsId);
            SetSuccess("Guide settings saved.");
            return RedirectToAction(nameof(Settings));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Failed to save guide settings for EventSettingsId {EventSettingsId}", model.EventSettingsId);
            ModelState.AddModelError("", ex.Message);
            model.AvailableEventSettings = await BuildEventSettingsOptionsAsync();
            return View(nameof(Settings), model);
        }
    }

    // ─── Event Categories ─────────────────────────────────────────

    [HttpGet("Categories")]
    public async Task<IActionResult> Categories()
    {
        var categories = await _guide.GetAllCategoriesAsync();
        var rows = categories.Select(c => new EventCategoryRowViewModel
        {
            Id = c.Id,
            Name = c.Name,
            Slug = c.Slug,
            IsSensitive = c.IsSensitive,
            IsActive = c.IsActive,
            DisplayOrder = c.DisplayOrder,
            EventCount = c.Events.Count
        }).ToList();

        return View(new EventCategoryListViewModel { Categories = rows });
    }

    [HttpGet("Categories/Create")]
    public async Task<IActionResult> CreateCategory()
    {
        return View("CategoryForm", new EventCategoryFormViewModel
        {
            DisplayOrder = await _guide.GetNextCategoryOrderAsync()
        });
    }

    [HttpPost("Categories/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateCategory(EventCategoryFormViewModel model)
    {
        if (!ModelState.IsValid)
            return View("CategoryForm", model);

        if (await _guide.CategorySlugExistsAsync(model.Slug))
        {
            ModelState.AddModelError(nameof(model.Slug), "A category with this slug already exists.");
            return View("CategoryForm", model);
        }

        var category = new EventCategory
        {
            Id = Guid.NewGuid(),
            Name = model.Name,
            Slug = model.Slug,
            IsSensitive = model.IsSensitive,
            IsActive = model.IsActive,
            DisplayOrder = model.DisplayOrder
        };

        await _guide.CreateCategoryAsync(category);
        _logger.LogInformation("Category '{Name}' created with slug '{Slug}'", model.Name, model.Slug);
        SetSuccess($"Category \"{model.Name}\" created.");
        return RedirectToAction(nameof(Categories));
    }

    [HttpGet("Categories/{id:guid}/Edit")]
    public async Task<IActionResult> EditCategory(Guid id)
    {
        var category = await _guide.GetCategoryAsync(id);
        if (category == null) return NotFound();

        return View("CategoryForm", new EventCategoryFormViewModel
        {
            Id = category.Id,
            Name = category.Name,
            Slug = category.Slug,
            IsSensitive = category.IsSensitive,
            IsActive = category.IsActive,
            DisplayOrder = category.DisplayOrder
        });
    }

    [HttpPost("Categories/{id:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditCategory(Guid id, EventCategoryFormViewModel model)
    {
        if (!ModelState.IsValid)
        {
            model.Id = id;
            return View("CategoryForm", model);
        }

        var category = await _guide.GetCategoryAsync(id);
        if (category == null) return NotFound();

        if (await _guide.CategorySlugExistsAsync(model.Slug, id))
        {
            ModelState.AddModelError(nameof(model.Slug), "A category with this slug already exists.");
            model.Id = id;
            return View("CategoryForm", model);
        }

        category.Name = model.Name;
        category.Slug = model.Slug;
        category.IsSensitive = model.IsSensitive;
        category.IsActive = model.IsActive;
        category.DisplayOrder = model.DisplayOrder;

        await _guide.UpdateCategoryAsync(category);
        _logger.LogInformation("Category '{Name}' ({Id}) updated", model.Name, id);
        SetSuccess($"Category \"{model.Name}\" updated.");
        return RedirectToAction(nameof(Categories));
    }

    [HttpPost("Categories/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCategory(Guid id)
    {
        var (deleted, linkedCount) = await _guide.DeleteCategoryAsync(id);
        if (linkedCount > 0)
        {
            SetError($"Cannot delete this category — it has {linkedCount} associated event(s).");
            return RedirectToAction(nameof(Categories));
        }
        if (!deleted) return NotFound();

        _logger.LogInformation("Category ({Id}) deleted", id);
        SetSuccess("Category deleted.");
        return RedirectToAction(nameof(Categories));
    }

    [HttpPost("Categories/{id:guid}/MoveUp")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveCategoryUp(Guid id)
    {
        await _guide.MoveCategoryAsync(id, -1);
        return RedirectToAction(nameof(Categories));
    }

    [HttpPost("Categories/{id:guid}/MoveDown")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveCategoryDown(Guid id)
    {
        await _guide.MoveCategoryAsync(id, +1);
        return RedirectToAction(nameof(Categories));
    }

    // ─── Guide Shared Venues ──────────────────────────────────────

    [HttpGet("Venues")]
    public async Task<IActionResult> Venues()
    {
        var venues = await _guide.GetAllVenuesAsync();
        var rows = venues.Select(v => new GuideVenueRowViewModel
        {
            Id = v.Id,
            Name = v.Name,
            LocationDescription = v.LocationDescription,
            IsActive = v.IsActive,
            DisplayOrder = v.DisplayOrder,
            EventCount = v.Events.Count
        }).ToList();

        return View(new GuideVenueListViewModel { Venues = rows });
    }

    [HttpGet("Venues/Create")]
    public async Task<IActionResult> CreateVenue()
    {
        return View("VenueForm", new GuideVenueFormViewModel
        {
            DisplayOrder = await _guide.GetNextVenueOrderAsync()
        });
    }

    [HttpPost("Venues/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateVenue(GuideVenueFormViewModel model)
    {
        if (!ModelState.IsValid)
            return View("VenueForm", model);

        var venue = new EventVenue
        {
            Id = Guid.NewGuid(),
            Name = model.Name,
            Description = model.Description,
            LocationDescription = model.LocationDescription,
            IsActive = model.IsActive,
            DisplayOrder = model.DisplayOrder
        };

        await _guide.CreateVenueAsync(venue);
        _logger.LogInformation("Venue '{Name}' created", model.Name);
        SetSuccess($"Venue \"{model.Name}\" created.");
        return RedirectToAction(nameof(Venues));
    }

    [HttpGet("Venues/{id:guid}/Edit")]
    public async Task<IActionResult> EditVenue(Guid id)
    {
        var venue = await _guide.GetVenueAsync(id);
        if (venue == null) return NotFound();

        return View("VenueForm", new GuideVenueFormViewModel
        {
            Id = venue.Id,
            Name = venue.Name,
            Description = venue.Description,
            LocationDescription = venue.LocationDescription,
            IsActive = venue.IsActive,
            DisplayOrder = venue.DisplayOrder
        });
    }

    [HttpPost("Venues/{id:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditVenue(Guid id, GuideVenueFormViewModel model)
    {
        if (!ModelState.IsValid)
        {
            model.Id = id;
            return View("VenueForm", model);
        }

        var venue = await _guide.GetVenueAsync(id);
        if (venue == null) return NotFound();

        venue.Name = model.Name;
        venue.Description = model.Description;
        venue.LocationDescription = model.LocationDescription;
        venue.IsActive = model.IsActive;
        venue.DisplayOrder = model.DisplayOrder;

        await _guide.UpdateVenueAsync(venue);
        _logger.LogInformation("Venue '{Name}' ({Id}) updated", model.Name, id);
        SetSuccess($"Venue \"{model.Name}\" updated.");
        return RedirectToAction(nameof(Venues));
    }

    [HttpPost("Venues/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteVenue(Guid id)
    {
        var (deleted, linkedCount) = await _guide.DeleteVenueAsync(id);
        if (linkedCount > 0)
        {
            SetError($"Cannot delete this venue — it has {linkedCount} associated event(s).");
            return RedirectToAction(nameof(Venues));
        }
        if (!deleted) return NotFound();

        _logger.LogInformation("Venue ({Id}) deleted", id);
        SetSuccess("Venue deleted.");
        return RedirectToAction(nameof(Venues));
    }

    [HttpPost("Venues/{id:guid}/MoveUp")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveVenueUp(Guid id)
    {
        await _guide.MoveVenueAsync(id, -1);
        return RedirectToAction(nameof(Venues));
    }

    [HttpPost("Venues/{id:guid}/MoveDown")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveVenueDown(Guid id)
    {
        await _guide.MoveVenueAsync(id, +1);
        return RedirectToAction(nameof(Venues));
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private async Task<List<EventSettingsOptionViewModel>> BuildEventSettingsOptionsAsync()
    {
        var options = await _guide.GetEventSettingsOptionsAsync();
        return options.Select(e => new EventSettingsOptionViewModel { Id = e.Id, EventName = e.EventName }).ToList();
    }

}
