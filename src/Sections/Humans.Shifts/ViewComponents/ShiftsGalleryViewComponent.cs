using System.Security.Claims;
using Humans.Shifts.Contracts;
using Humans.Shifts.Domain;
using Humans.Shifts.Models;
using Humans.Shifts.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Shifts.ViewComponents;

/// <summary>
/// A view component, not a partial, because a partial's model is section-internal and Debug can't name it. A
/// chrome-slot contribution takes no arguments, so it reads the user from claims.
/// </summary>
internal sealed class ShiftsGalleryViewComponent(
    IShiftManagement shiftMgmt,
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
            var depts = (await shiftMgmt.GetDepartmentsWithRotasAsync(es.Id, HttpContext.RequestAborted))
                .OrderBy(d => d.TeamName, StringComparer.Ordinal)
                .ToList();
            if (depts.Count > 0)
            {
                sampleDeptId = depts[0].TeamId;
                var rotas = await shiftMgmt.GetRotasByDepartmentAsync(sampleDeptId.Value, es.Id);
                rota = rotas.Select(r => r.Rota).FirstOrDefault();
            }

            var sampleRotaShifts = new List<ShiftDisplayItem>();
            if (rota is not null && sampleDeptId is not null)
            {
                var browse = await shiftMgmt.GetBrowseShiftsAsync(new ShiftBrowseQuery(
                    es.Id,
                    sampleDeptId.Value,
                    Flags: ShiftBrowseQueryFlags.IncludeSignups), HttpContext.RequestAborted);
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
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to resolve shifts samples for widget gallery: {Reason}", ex.Message);
            return View(ShiftsGalleryViewModel.Empty with { CurrentUserId = currentUserId });
        }
    }
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
