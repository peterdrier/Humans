using Humans.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Humans.Infrastructure.Data;
using Humans.Domain.Entities;
using Humans.Web.Models;
using NodaTime;
using Humans.Web.Filters;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
[Route("Admin")]
[ServiceFilter(typeof(EventGuideFeatureFilter))]
public class GuideAdminController : HumansControllerBase
{
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;
    private readonly ILogger<GuideAdminController> _logger;

    public GuideAdminController(
        HumansDbContext dbContext,
        IClock clock,
        ILogger<GuideAdminController> logger,
        UserManager<User> userManager)
        : base(userManager)
    {
        _dbContext = dbContext;
        _clock = clock;
        _logger = logger;
    }

    // ─── GuideSettings ────────────────────────────────────────────

    [HttpGet("GuideSettings")]
    public async Task<IActionResult> GuideSettings()
    {
        var existing = await _dbContext.GuideSettings
            .Include(g => g.EventSettings)
            .FirstOrDefaultAsync();

        var eventSettingsOptions = await _dbContext.EventSettings
            .Where(e => e.IsActive)
            .OrderByDescending(e => e.GateOpeningDate)
            .Select(e => new EventSettingsOptionViewModel { Id = e.Id, EventName = e.EventName })
            .ToListAsync();

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
            model.AvailableEventSettings = await GetEventSettingsOptions();
            return View(nameof(GuideSettings), model);
        }

        var eventSettings = await _dbContext.EventSettings.FindAsync(model.EventSettingsId);
        if (eventSettings == null)
        {
            ModelState.AddModelError(nameof(model.EventSettingsId), "Selected event edition not found.");
            model.AvailableEventSettings = await GetEventSettingsOptions();
            return View(nameof(GuideSettings), model);
        }

        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventSettings.TimeZoneId);
        var now = _clock.GetCurrentInstant();

        var existing = await _dbContext.GuideSettings.FirstOrDefaultAsync();

        if (existing == null)
        {
            var settings = new GuideSettings
            {
                Id = Guid.NewGuid(),
                EventSettingsId = model.EventSettingsId,
                SubmissionOpenAt = ToInstant(model.SubmissionOpenAt, tz),
                SubmissionCloseAt = ToInstant(model.SubmissionCloseAt, tz),
                GuidePublishAt = ToInstant(model.GuidePublishAt, tz),
                MaxPrintSlots = model.MaxPrintSlots,
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.GuideSettings.Add(settings);
            _logger.LogInformation("Guide settings created for event {EventSettingsId}", model.EventSettingsId);
        }
        else
        {
            existing.EventSettingsId = model.EventSettingsId;
            existing.SubmissionOpenAt = ToInstant(model.SubmissionOpenAt, tz);
            existing.SubmissionCloseAt = ToInstant(model.SubmissionCloseAt, tz);
            existing.GuidePublishAt = ToInstant(model.GuidePublishAt, tz);
            existing.MaxPrintSlots = model.MaxPrintSlots;
            existing.UpdatedAt = now;
            _logger.LogInformation("Guide settings updated for event {EventSettingsId}", model.EventSettingsId);
        }

        await _dbContext.SaveChangesAsync();
        SetSuccess("Guide settings saved.");
        return RedirectToAction(nameof(GuideSettings));
    }

    // ─── Event Categories ─────────────────────────────────────────

    [HttpGet("GuideCategories")]
    public async Task<IActionResult> GuideCategories()
    {
        var categories = await _dbContext.EventCategories
            .OrderBy(c => c.DisplayOrder)
            .ThenBy(c => c.Name)
            .Select(c => new EventCategoryRowViewModel
            {
                Id = c.Id,
                Name = c.Name,
                Slug = c.Slug,
                IsSensitive = c.IsSensitive,
                IsActive = c.IsActive,
                DisplayOrder = c.DisplayOrder,
                EventCount = c.GuideEvents.Count
            })
            .ToListAsync();

        return View(new EventCategoryListViewModel { Categories = categories });
    }

    [HttpGet("GuideCategories/Create")]
    public async Task<IActionResult> CreateCategory()
    {
        var maxOrder = await _dbContext.EventCategories
            .Select(c => (int?)c.DisplayOrder)
            .MaxAsync() ?? 0;

        return View("GuideCategoryForm", new EventCategoryFormViewModel
        {
            DisplayOrder = maxOrder + 1
        });
    }

    [HttpPost("GuideCategories/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateCategory(EventCategoryFormViewModel model)
    {
        if (!ModelState.IsValid)
            return View("GuideCategoryForm", model);

        if (await SlugExistsAsync(model.Slug, null))
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

        _dbContext.EventCategories.Add(category);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Category '{Name}' created with slug '{Slug}'", model.Name, model.Slug);
        SetSuccess($"Category \"{model.Name}\" created.");
        return RedirectToAction(nameof(GuideCategories));
    }

    [HttpGet("GuideCategories/{id:guid}/Edit")]
    public async Task<IActionResult> EditCategory(Guid id)
    {
        var category = await _dbContext.EventCategories.FindAsync(id);
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

        var category = await _dbContext.EventCategories.FindAsync(id);
        if (category == null) return NotFound();

        if (await SlugExistsAsync(model.Slug, id))
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

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Category '{Name}' ({Id}) updated", model.Name, id);
        SetSuccess($"Category \"{model.Name}\" updated.");
        return RedirectToAction(nameof(GuideCategories));
    }

    [HttpPost("GuideCategories/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCategory(Guid id)
    {
        var category = await _dbContext.EventCategories
            .Include(c => c.GuideEvents)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (category == null) return NotFound();

        if (category.GuideEvents.Count > 0)
        {
            SetError($"Cannot delete \"{category.Name}\" — it has {category.GuideEvents.Count} associated event(s).");
            return RedirectToAction(nameof(GuideCategories));
        }

        _dbContext.EventCategories.Remove(category);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Category '{Name}' ({Id}) deleted", category.Name, id);
        SetSuccess($"Category \"{category.Name}\" deleted.");
        return RedirectToAction(nameof(GuideCategories));
    }

    [HttpPost("GuideCategories/{id:guid}/MoveUp")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveCategoryUp(Guid id)
    {
        await SwapCategoryOrder(id, -1);
        return RedirectToAction(nameof(GuideCategories));
    }

    [HttpPost("GuideCategories/{id:guid}/MoveDown")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveCategoryDown(Guid id)
    {
        await SwapCategoryOrder(id, +1);
        return RedirectToAction(nameof(GuideCategories));
    }

    // ─── Guide Shared Venues ──────────────────────────────────────

    [HttpGet("GuideVenues")]
    public async Task<IActionResult> GuideVenues()
    {
        var venues = await _dbContext.GuideSharedVenues
            .OrderBy(v => v.DisplayOrder)
            .ThenBy(v => v.Name)
            .Select(v => new GuideVenueRowViewModel
            {
                Id = v.Id,
                Name = v.Name,
                LocationDescription = v.LocationDescription,
                IsActive = v.IsActive,
                DisplayOrder = v.DisplayOrder,
                EventCount = v.GuideEvents.Count
            })
            .ToListAsync();

        return View(new GuideVenueListViewModel { Venues = venues });
    }

    [HttpGet("GuideVenues/Create")]
    public async Task<IActionResult> CreateVenue()
    {
        var maxOrder = await _dbContext.GuideSharedVenues
            .Select(v => (int?)v.DisplayOrder)
            .MaxAsync() ?? 0;

        return View("GuideVenueForm", new GuideVenueFormViewModel
        {
            DisplayOrder = maxOrder + 1
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

        _dbContext.GuideSharedVenues.Add(venue);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Venue '{Name}' created", model.Name);
        SetSuccess($"Venue \"{model.Name}\" created.");
        return RedirectToAction(nameof(GuideVenues));
    }

    [HttpGet("GuideVenues/{id:guid}/Edit")]
    public async Task<IActionResult> EditVenue(Guid id)
    {
        var venue = await _dbContext.GuideSharedVenues.FindAsync(id);
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

        var venue = await _dbContext.GuideSharedVenues.FindAsync(id);
        if (venue == null) return NotFound();

        venue.Name = model.Name;
        venue.Description = model.Description;
        venue.LocationDescription = model.LocationDescription;
        venue.IsActive = model.IsActive;
        venue.DisplayOrder = model.DisplayOrder;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Venue '{Name}' ({Id}) updated", model.Name, id);
        SetSuccess($"Venue \"{model.Name}\" updated.");
        return RedirectToAction(nameof(GuideVenues));
    }

    [HttpPost("GuideVenues/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteVenue(Guid id)
    {
        var venue = await _dbContext.GuideSharedVenues
            .Include(v => v.GuideEvents)
            .FirstOrDefaultAsync(v => v.Id == id);

        if (venue == null) return NotFound();

        if (venue.GuideEvents.Count > 0)
        {
            SetError($"Cannot delete \"{venue.Name}\" — it has {venue.GuideEvents.Count} associated event(s).");
            return RedirectToAction(nameof(GuideVenues));
        }

        _dbContext.GuideSharedVenues.Remove(venue);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Venue '{Name}' ({Id}) deleted", venue.Name, id);
        SetSuccess($"Venue \"{venue.Name}\" deleted.");
        return RedirectToAction(nameof(GuideVenues));
    }

    [HttpPost("GuideVenues/{id:guid}/MoveUp")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveVenueUp(Guid id)
    {
        await SwapVenueOrder(id, -1);
        return RedirectToAction(nameof(GuideVenues));
    }

    [HttpPost("GuideVenues/{id:guid}/MoveDown")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveVenueDown(Guid id)
    {
        await SwapVenueOrder(id, +1);
        return RedirectToAction(nameof(GuideVenues));
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private async Task<List<EventSettingsOptionViewModel>> GetEventSettingsOptions()
    {
        return await _dbContext.EventSettings
            .Where(e => e.IsActive)
            .OrderByDescending(e => e.GateOpeningDate)
            .Select(e => new EventSettingsOptionViewModel { Id = e.Id, EventName = e.EventName })
            .ToListAsync();
    }

    private async Task<bool> SlugExistsAsync(string slug, Guid? excludeId)
    {
        var query = _dbContext.EventCategories.Where(c => c.Slug == slug);
        if (excludeId.HasValue)
            query = query.Where(c => c.Id != excludeId.Value);
        return await query.AnyAsync();
    }

    private async Task SwapCategoryOrder(Guid id, int direction)
    {
        var categories = await _dbContext.EventCategories
            .OrderBy(c => c.DisplayOrder)
            .ThenBy(c => c.Name)
            .ToListAsync();

        var index = categories.FindIndex(c => c.Id == id);
        if (index < 0) return;

        var targetIndex = index + direction;
        if (targetIndex < 0 || targetIndex >= categories.Count) return;

        (categories[index].DisplayOrder, categories[targetIndex].DisplayOrder) =
            (categories[targetIndex].DisplayOrder, categories[index].DisplayOrder);

        await _dbContext.SaveChangesAsync();
    }

    private async Task SwapVenueOrder(Guid id, int direction)
    {
        var venues = await _dbContext.GuideSharedVenues
            .OrderBy(v => v.DisplayOrder)
            .ThenBy(v => v.Name)
            .ToListAsync();

        var index = venues.FindIndex(v => v.Id == id);
        if (index < 0) return;

        var targetIndex = index + direction;
        if (targetIndex < 0 || targetIndex >= venues.Count) return;

        (venues[index].DisplayOrder, venues[targetIndex].DisplayOrder) =
            (venues[targetIndex].DisplayOrder, venues[index].DisplayOrder);

        await _dbContext.SaveChangesAsync();
    }

    private static DateTime ToLocalDateTime(Instant instant, DateTimeZone? tz)
    {
        if (tz == null)
            return instant.ToDateTimeUtc();

        return instant.InZone(tz).ToDateTimeUnspecified();
    }

    private static Instant ToInstant(DateTime dateTime, DateTimeZone? tz)
    {
        if (tz == null)
            return Instant.FromDateTimeUtc(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc));

        var local = LocalDateTime.FromDateTime(dateTime);
        return local.InZoneLeniently(tz).ToInstant();
    }
}
