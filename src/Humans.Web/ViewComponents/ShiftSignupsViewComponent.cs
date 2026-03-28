using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Web.Models;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.ViewComponents;

public class ShiftSignupsViewComponent : ViewComponent
{
    private readonly IShiftSignupService _signupService;
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IClock _clock;

    public ShiftSignupsViewComponent(
        IShiftSignupService signupService,
        IShiftManagementService shiftMgmt,
        IClock clock)
    {
        _signupService = signupService;
        _shiftMgmt = shiftMgmt;
        _clock = clock;
    }

    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ShiftSignupsViewMode viewMode, string? displayName = null)
    {
        var es = await _shiftMgmt.GetActiveAsync();

        var signups = es is not null
            ? await _signupService.GetByUserAsync(userId, es.Id)
            : [];

        var now = _clock.GetCurrentInstant();
        var model = new ShiftSignupsViewModel
        {
            EventSettings = es,
            ViewMode = viewMode,
            UserId = userId,
            DisplayName = displayName
        };

        foreach (var signup in signups)
        {
            if (signup.Shift?.Rota?.Team is null || es is null)
                continue;

            var item = new MySignupItem
            {
                Signup = signup,
                DepartmentName = signup.Shift.Rota.Team.Name,
                AbsoluteStart = signup.Shift.GetAbsoluteStart(es),
                AbsoluteEnd = signup.Shift.GetAbsoluteEnd(es)
            };

            switch (signup.Status)
            {
                case SignupStatus.Confirmed when item.AbsoluteStart > now:
                    model.Upcoming.Add(item);
                    break;
                case SignupStatus.Pending:
                    model.Pending.Add(item);
                    break;
                default:
                    if (signup.Status is SignupStatus.Confirmed or SignupStatus.NoShow or SignupStatus.Bailed)
                        model.Past.Add(item);
                    break;
            }
        }

        model.Upcoming = model.Upcoming.OrderBy(s => s.AbsoluteStart).ToList();
        model.Pending = model.Pending.OrderBy(s => s.AbsoluteStart).ToList();
        model.Past = model.Past.OrderByDescending(s => s.AbsoluteStart).ToList();

        return View(model);
    }
}
