using Humans.Tickets.Contracts;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

using Humans.Base.Authorization;
using Humans.Base.Models;
using Humans.Users.Contracts;

namespace Humans.Debug.Controllers;

/// <summary>
/// Admin-only catalog of every reusable UI widget — TagHelpers, ViewComponents, and
/// shared partials — rendered against hard-coded sample data so designers and developers
/// can see what exists, what it's called, and how it looks filled in. Companion to
/// <c>/ColorPalette</c>. Admin dev tool — linked from the admin sidebar "Design" group.
/// This controller resolves sample data for Base and Shell widgets only; each section's
/// own widgets arrive through the <c>ChromeSlots.WidgetGallery</c> chrome slot.
/// </summary>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Debug/WidgetGallery")]
internal sealed class WidgetGalleryController(IUserService userService) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var (error, currentUser) = await RequireCurrentUserAsync();
        if (error is not null)
            return error;

        var displayName = string.IsNullOrEmpty(currentUser.BurnerName)
            ? "Current user"
            : currentUser.BurnerName;

        var model = new WidgetGalleryViewModel
        {
            CurrentUserId = currentUser.Id,
            CurrentUserDisplayName = displayName,
            SamplePager = new PagerViewModel(totalPages: 8, currentPage: 3, action: "Index"),
            SampleTableRows =
            [
                new() { Name = "Sparkle", Amount = 120.50m, JoinedAt = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromDays(400)), Status = TicketAttendeeStatus.Valid, IsVip = true },
                new() { Name = "Embers", Amount = 95.00m, JoinedAt = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromDays(120)), Status = TicketAttendeeStatus.CheckedIn, IsVip = false },
                new() { Name = "Dusty", Amount = 240.00m, JoinedAt = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromDays(30)), Status = TicketAttendeeStatus.Void, IsVip = false },
                new() { Name = "Nova", Amount = 95.00m, JoinedAt = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromDays(10)), Status = TicketAttendeeStatus.Valid, IsVip = false },
            ],
        };

        return View(model);
    }
}

internal sealed class WidgetGalleryViewModel
{
    public required Guid CurrentUserId { get; init; }
    public required string CurrentUserDisplayName { get; init; }
    public required PagerViewModel SamplePager { get; init; }
    public List<TableDemoRow> SampleTableRows { get; set; } = [];
}

internal sealed class TableDemoRow
{
    public string Name { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public Instant JoinedAt { get; set; }
    public TicketAttendeeStatus Status { get; set; }
    public bool IsVip { get; set; }
}
