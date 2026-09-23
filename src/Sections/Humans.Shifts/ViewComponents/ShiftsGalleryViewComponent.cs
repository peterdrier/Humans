using System.Security.Claims;
using Humans.Shifts.Contracts;
using Humans.Shifts.Domain;
using Humans.Shifts.Models;
using Humans.Shifts.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Shifts.ViewComponents;

/// <summary>
/// The widget gallery's samples, contributed to
/// <see cref="Humans.Base.Interfaces.ChromeSlots.WidgetGallery"/> via <see cref="SectionChrome"/>:
/// four rota cards — <c>_RotaHeader</c>, <c>_RotaBadges</c>, <c>_RotaDetails</c> and
/// <c>_EventRotaTable</c> — rendered against live samples from the active event, plus
/// &lt;vc:dietary-missing-banner&gt;.
/// </summary>
/// <remarks>
/// A view component rather than a partial because a partial needs a model and Debug would have
/// to be able to name it; these four cards are the only ones in the gallery that construct
/// section-internal types. Stays <c>internal</c> (design §15 step 6). A chrome-slot contribution
/// takes no arguments (<c>ISectionChrome</c>'s <c>[ViewComponentSlot]</c> declares none), so it
/// reads the current user from claims rather than a caller-supplied id.
/// </remarks>
internal sealed class ShiftsGalleryViewComponent(
    IShiftManagementService shiftMgmt,
    IBurnSettingsService burnSettings,
    ILogger<ShiftsGalleryViewComponent> logger) : ViewComponent
{
    private const int SampleShiftCount = 8;

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var currentUserId = Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : Guid.Empty;

        try
        {
            var es = await burnSettings.GetActiveAsync(HttpContext.RequestAborted);
            if (es is null)
                return View(ShiftsGalleryViewModel.Empty with { CurrentUserId = currentUserId });

            RotaInfo? rota = null;
            Guid? sampleDeptId = null;
            var depts = await shiftMgmt.GetDepartmentsWithRotasAsync(es.Id);
            if (depts.Count > 0)
            {
                sampleDeptId = depts[0].TeamId;
                var rotas = await shiftMgmt.GetRotasByDepartmentAsync(sampleDeptId.Value, es.Id);
                rota = rotas.Select(ToSampleRotaInfo).FirstOrDefault();
            }

            var sampleRotaShifts = new List<ShiftDisplayItem>();
            if (rota is not null && sampleDeptId is not null)
            {
                var browse = await shiftMgmt.GetBrowseShiftsAsync(new ShiftBrowseQuery(
                    es.Id,
                    sampleDeptId.Value,
                    Flags: ShiftBrowseQueryFlags.IncludeSignups));
                sampleRotaShifts = browse
                    .Where(u => u.Shift.RotaId == rota.Id)
                    .OrderBy(u => u.Shift.DayOffset)
                    .ThenBy(u => u.Shift.StartTime)
                    .Take(SampleShiftCount)
                    .Select(ShiftBrowseMapper.MapToDisplayItem)
                    .ToList();
            }

            var userSignupShiftIds = sampleRotaShifts
                .Where(s => s.Signups.Any(sig => sig.UserId == currentUserId))
                .Select(s => s.Shift.Id)
                .ToHashSet();

            return View(new ShiftsGalleryViewModel
            {
                CurrentUserId = currentUserId,
                EventSettings = es,
                Rota = rota,
                RotaShifts = sampleRotaShifts,
                UserSignupShiftIds = userSignupShiftIds,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to resolve shifts samples for widget gallery: {Reason}", ex.Message);
            return View(ShiftsGalleryViewModel.Empty with { CurrentUserId = currentUserId });
        }
    }

    private static RotaInfo ToSampleRotaInfo(Rota rota) =>
        new(
            rota.Id,
            rota.EventSettingsId,
            rota.TeamId,
            rota.Name,
            rota.Description,
            rota.PracticalInfo,
            rota.Priority,
            rota.Policy,
            rota.Period,
            rota.IsVisibleToVolunteers,
            rota.Tags.Select(t => new ShiftTagSummary(t.Id, t.Name)).ToList());
}

internal sealed record ShiftsGalleryViewModel
{
    public Guid CurrentUserId { get; init; }
    public BurnSettingsInfo? EventSettings { get; init; }
    public RotaInfo? Rota { get; init; }
    public IReadOnlyList<ShiftDisplayItem> RotaShifts { get; init; } = [];
    public IReadOnlySet<Guid> UserSignupShiftIds { get; init; } = new HashSet<Guid>();

    public static readonly ShiftsGalleryViewModel Empty = new();
}
