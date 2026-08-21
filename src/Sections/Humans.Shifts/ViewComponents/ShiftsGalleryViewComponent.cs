using Humans.Shifts.Contracts;
using Humans.Shifts.Domain;
using Humans.Shifts.Models;
using Humans.Shifts.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Shifts.ViewComponents;

/// <summary>
/// The widget gallery's four rota cards — <c>_RotaHeader</c>, <c>_RotaBadges</c>,
/// <c>_RotaDetails</c> and <c>_EventRotaTable</c> — rendered against live samples from
/// the active event.
/// </summary>
/// <remarks>
/// A view component rather than a partial because a partial needs a model and Shell would
/// have to be able to name it; these four cards are the only ones in the gallery that
/// construct section-internal types. Invoked by name from
/// <c>WidgetGallery/Index.cshtml</c> and discovered by Shell's
/// <c>SectionViewComponentFeatureProvider</c>, so it stays <c>internal</c>
/// (design §15 step 6; nobodies-collective/Humans#866). Its one parameter is a
/// <see cref="Guid"/>, which Shell can name — Governance's rider on invoke-by-name.
///
/// <para>
/// It resolves its own samples, which is what took <c>GetRotasByDepartmentAsync</c> — the
/// last entity-returning member — off the contracts leaf, and what deleted
/// <c>WidgetGalleryController.MapToDisplayItem</c>, a hand-rolled duplicate of
/// <see cref="ShiftBrowseMapper.MapToDisplayItem"/>.
/// </para>
/// </remarks>
internal sealed class ShiftsGalleryViewComponent(
    IShiftManagementService shiftMgmt,
    IBurnSettingsService burnSettings,
    ILogger<ShiftsGalleryViewComponent> logger) : ViewComponent
{
    private const int SampleShiftCount = 8;

    public async Task<IViewComponentResult> InvokeAsync(Guid currentUserId)
    {
        try
        {
            var es = await burnSettings.GetActiveAsync(HttpContext.RequestAborted);
            if (es is null)
                return View(ShiftsGalleryViewModel.Empty);

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
                EventSettings = es,
                Rota = rota,
                RotaShifts = sampleRotaShifts,
                UserSignupShiftIds = userSignupShiftIds,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to resolve shifts samples for widget gallery: {Reason}", ex.Message);
            return View(ShiftsGalleryViewModel.Empty);
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

internal sealed class ShiftsGalleryViewModel
{
    public BurnSettingsInfo? EventSettings { get; init; }
    public RotaInfo? Rota { get; init; }
    public IReadOnlyList<ShiftDisplayItem> RotaShifts { get; init; } = [];
    public IReadOnlySet<Guid> UserSignupShiftIds { get; init; } = new HashSet<Guid>();

    public static readonly ShiftsGalleryViewModel Empty = new();
}
