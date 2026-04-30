using Humans.Application.Interfaces.EventGuide;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Web.Filters;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
[Route("Admin")]
[ServiceFilter(typeof(EventGuideFeatureFilter))]
public class GuideAdminController : HumansControllerBase
{
    private readonly IEventGuideService _guide;
    private readonly ILogger<GuideAdminController> _logger;

    public GuideAdminController(
        IEventGuideService guide,
        ILogger<GuideAdminController> logger,
        UserManager<User> userManager)
        : base(userManager)
    {
        _guide = guide;
        _logger = logger;
    }

    // ─── GuideSettings ────────────────────────────────────────────

    [HttpGet("GuideSettings")]
    public async Task<IActionResult> GuideSettings()
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

        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(existing.EventSettings.TimeZoneId);
        return View(new GuideSettingsViewModel
        {
            Id = existing.Id,
            EventSettingsId = existing.EventSettingsId,
            SubmissionOpenAt = ToLocalDateTime(existing.SubmissionOpenAt, tz),
            SubmissionCloseAt = ToLocalDateTime(existing.SubmissionCloseAt, tz),
            GuidePublishAt = ToLocalDateTime(existing.GuidePublishAt, tz),
            MaxPrintSlots = existing.MaxPrintSlots,
            AvailableEventSettings = eventSettingsOptions,
            TimeZoneId = existing.EventSettings.TimeZoneId
        });
    }

    [HttpPost("GuideSettings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveGuideSettings(GuideSettingsViewModel model)
    {
        if (!ModelState.IsValid)
        {
            model.AvailableEventSettings = await BuildEventSettingsOptionsAsync();
            return View(nameof(GuideSettings), model);
        }

        var eventSettings = await _guide.GetEventSettingsByIdAsync(model.EventSettingsId);
        if (eventSettings == null)
        {
            ModelState.AddModelError(nameof(model.EventSettingsId), "Selected event edition not found.");
            model.AvailableEventSettings = await BuildEventSettingsOptionsAsync();
            return View(nameof(GuideSettings), model);
        }

        try
        {
            await _guide.SaveGuideSettingsAsync(
                model.Id == Guid.Empty ? null : model.Id,
                model.EventSettingsId,
                model.SubmissionOpenAt,
                model.SubmissionCloseAt,
                model.GuidePublishAt,
                model.MaxPrintSlots);

            _logger.LogInformation("Guide settings saved for event {EventSettingsId}", model.EventSettingsId);
            SetSuccess("Guide settings saved.");
            return RedirectToAction(nameof(GuideSettings));
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError("", ex.Message);
            model.AvailableEventSettings = await BuildEventSettingsOptionsAsync();
            return View(nameof(GuideSettings), model);
        }
    }

    // ─── Event Categories ─────────────────────────────────────────

    [HttpGet("GuideCategories")]
    public async Task<IActionResult> GuideCategories()
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
            EventCount = c.GuideEvents.Count
        }).ToList();

        return View(new EventCategoryListViewModel { Categories = rows });
    }

    [HttpGet("GuideCategories/Create")]
    public async Task<IActionResult> CreateCategory()
    {
        return View("GuideCategoryForm", new EventCategoryFormViewModel
        {
            DisplayOrder = await _guide.GetNextCategoryOrderAsync()
        });
    }

    [HttpPost("GuideCategories/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateCategory(EventCategoryFormViewModel model)
    {
        if (!ModelState.IsValid)
            return View("GuideCategoryForm", model);

        if (await _guide.CategorySlugExistsAsync(model.Slug))
        {
            ModelState.AddModelError(nameof(model.Slug), "A category with this slug already exists.");
            return View("GuideCategoryForm", model);
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
        return RedirectToAction(nameof(GuideCategories));
    }

    [HttpGet("GuideCategories/{id:guid}/Edit")]
    public async Task<IActionResult> EditCategory(Guid id)
    {
        var category = await _guide.GetCategoryAsync(id);
        if (category == null) return NotFound();

        return View("GuideCategoryForm", new EventCategoryFormViewModel
        {
            Id = category.Id,
            Name = category.Name,
            Slug = category.Slug,
            IsSensitive = category.IsSensitive,
            IsActive = category.IsActive,
            DisplayOrder = category.DisplayOrder
        });
    }

    [HttpPost("GuideCategories/{id:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditCategory(Guid id, EventCategoryFormViewModel model)
    {
        if (!ModelState.IsValid)
        {
            model.Id = id;
            return View("GuideCategoryForm", model);
        }

        var category = await _guide.GetCategoryAsync(id);
        if (category == null) return NotFound();

        if (await _guide.CategorySlugExistsAsync(model.Slug, id))
        {
            ModelState.AddModelError(nameof(model.Slug), "A category with this slug already exists.");
            model.Id = id;
            return View("GuideCategoryForm", model);
        }

        category.Name = model.Name;
        category.Slug = model.Slug;
        category.IsSensitive = model.IsSensitive;
        category.IsActive = model.IsActive;
        category.DisplayOrder = model.DisplayOrder;

        await _guide.UpdateCategoryAsync(category);
        _logger.LogInformation("Category '{Name}' ({Id}) updated", model.Name, id);
        SetSuccess($"Category \"{model.Name}\" updated.");
        return RedirectToAction(nameof(GuideCategories));
    }

    [HttpPost("GuideCategories/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCategory(Guid id)
    {
        var (deleted, linkedCount) = await _guide.DeleteCategoryAsync(id);
        if (linkedCount > 0)
        {
            SetError($"Cannot delete this category — it has {linkedCount} associated event(s).");
            return RedirectToAction(nameof(GuideCategories));
        }
        if (!deleted) return NotFound();

        _logger.LogInformation("Category ({Id}) deleted", id);
        SetSuccess("Category deleted.");
        return RedirectToAction(nameof(GuideCategories));
    }

    [HttpPost("GuideCategories/{id:guid}/MoveUp")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveCategoryUp(Guid id)
    {
        await _guide.MoveCategoryAsync(id, -1);
        return RedirectToAction(nameof(GuideCategories));
    }

    [HttpPost("GuideCategories/{id:guid}/MoveDown")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveCategoryDown(Guid id)
    {
        await _guide.MoveCategoryAsync(id, +1);
        return RedirectToAction(nameof(GuideCategories));
    }

    // ─── Guide Shared Venues ──────────────────────────────────────

    [HttpGet("GuideVenues")]
    public async Task<IActionResult> GuideVenues()
    {
        var venues = await _guide.GetAllVenuesAsync();
        var rows = venues.Select(v => new GuideVenueRowViewModel
        {
            Id = v.Id,
            Name = v.Name,
            LocationDescription = v.LocationDescription,
            IsActive = v.IsActive,
            DisplayOrder = v.DisplayOrder,
            EventCount = v.GuideEvents.Count
        }).ToList();

        return View(new GuideVenueListViewModel { Venues = rows });
    }

    [HttpGet("GuideVenues/Create")]
    public async Task<IActionResult> CreateVenue()
    {
        return View("GuideVenueForm", new GuideVenueFormViewModel
        {
            DisplayOrder = await _guide.GetNextVenueOrderAsync()
        });
    }

    [HttpPost("GuideVenues/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateVenue(GuideVenueFormViewModel model)
    {
        if (!ModelState.IsValid)
            return View("GuideVenueForm", model);

        var venue = new GuideSharedVenue
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
        return RedirectToAction(nameof(GuideVenues));
    }

    [HttpGet("GuideVenues/{id:guid}/Edit")]
    public async Task<IActionResult> EditVenue(Guid id)
    {
        var venue = await _guide.GetVenueAsync(id);
        if (venue == null) return NotFound();

        return View("GuideVenueForm", new GuideVenueFormViewModel
        {
            Id = venue.Id,
            Name = venue.Name,
            Description = venue.Description,
            LocationDescription = venue.LocationDescription,
            IsActive = venue.IsActive,
            DisplayOrder = venue.DisplayOrder
        });
    }

    [HttpPost("GuideVenues/{id:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditVenue(Guid id, GuideVenueFormViewModel model)
    {
        if (!ModelState.IsValid)
        {
            model.Id = id;
            return View("GuideVenueForm", model);
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
        return RedirectToAction(nameof(GuideVenues));
    }

    [HttpPost("GuideVenues/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteVenue(Guid id)
    {
        var (deleted, linkedCount) = await _guide.DeleteVenueAsync(id);
        if (linkedCount > 0)
        {
            SetError($"Cannot delete this venue — it has {linkedCount} associated event(s).");
            return RedirectToAction(nameof(GuideVenues));
        }
        if (!deleted) return NotFound();

        _logger.LogInformation("Venue ({Id}) deleted", id);
        SetSuccess("Venue deleted.");
        return RedirectToAction(nameof(GuideVenues));
    }

    [HttpPost("GuideVenues/{id:guid}/MoveUp")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveVenueUp(Guid id)
    {
        await _guide.MoveVenueAsync(id, -1);
        return RedirectToAction(nameof(GuideVenues));
    }

    [HttpPost("GuideVenues/{id:guid}/MoveDown")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveVenueDown(Guid id)
    {
        await _guide.MoveVenueAsync(id, +1);
        return RedirectToAction(nameof(GuideVenues));
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private async Task<List<EventSettingsOptionViewModel>> BuildEventSettingsOptionsAsync()
    {
        var options = await _guide.GetEventSettingsOptionsAsync();
        return options.Select(e => new EventSettingsOptionViewModel { Id = e.Id, EventName = e.EventName }).ToList();
    }

    private static DateTime ToLocalDateTime(Instant instant, DateTimeZone? tz)
        => tz == null ? instant.ToDateTimeUtc() : instant.InZone(tz).ToDateTimeUnspecified();
}
