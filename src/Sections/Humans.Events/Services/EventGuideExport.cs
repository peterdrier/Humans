using System.Globalization;
using Humans.AuditLog.Contracts;
using Humans.Base.Csv;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Camps.Contracts;
using Humans.Events.Contracts;
using Humans.Events.Data;
using Humans.Events.Services.Dtos;
using Humans.Settings.Contracts;
using Humans.Users.Contracts;
using static Humans.Events.Helpers.EventsLookupHelpers;
using static Humans.Events.Helpers.EventsTimeHelpers;

namespace Humans.Events.Services;

internal sealed class EventGuideExport(
    IEventRepository repo,
    ISettingsService settingsService,
    IUserService userService,
    ICampService camps,
    IAuditLogService audit) : IApplicationService
{
    public async Task<byte[]> GetApprovedEventsForExportAsync(Guid actorUserId, CancellationToken ct = default)
    {
        var settings = await repo.GetGuideSettingsAsync(ct);
        var events = (await repo.GetApprovedEventsAsync(null, null, null, null, [], ct))
            .Where(e => e.IsInEvent(settings?.EventSettingsId)).Select(EventInfo.FromEvent).ToList();
        var eventSettings = settings is null ? null : await settingsService.GetEventSettingsByIdAsync(settings.EventSettingsId, ct);
        var tz = GetTimeZone(eventSettings);
        var campsById = await LoadCampsByIdAsync(camps, eventSettings?.Id, ct);
        var submitters = await LoadSubmittersAsync(
            userService, events.Where(e => e.CampId == null).Select(e => e.SubmitterUserId).Distinct(), ct);

        var rows = new List<object?[]>();
        foreach (var e in events.OrderBy(e => e.StartAt))
        {
            var camp = e.CampId.HasValue ? campsById.GetValueOrDefault(e.CampId.Value) : null;
            var campName = ResolveCampName(camp) ?? "";
            var venueName = e.VenueName ?? "";
            var submitterName = e.CampId == null
                ? submitters.GetValueOrDefault(e.SubmitterUserId)?.BurnerName ?? ""
                : "";

            foreach (var occurrence in e.GetOccurrenceInstants(eventSettings?.GateOpeningDate, tz))
            {
                var local = ToLocalDateTime(occurrence, tz);
                rows.Add([
                    e.Id.ToString(),
                    e.Title,
                    e.Description,
                    e.CategoryName,
                    campName,
                    venueName,
                    submitterName,
                    e.LocationNote ?? "",
                    local.ToInvariantDate(),
                    local.ToInvariantTime(),
                    e.DurationMinutes,
                    e.IsRecurring ? "Yes" : "No",
                    e.PriorityRank?.ToString(CultureInfo.InvariantCulture) ?? "",
                    e.Status.ToString(),
                    ToLocalDateTime(e.SubmittedAt, tz).ToInvariantTimestamp(),
                    e.Host ?? "",
                ]);
            }
        }

        var bytes = HumansCsv.WriteBytes(csv =>
        {
            csv.WriteRow("Id", "Title", "Description", "Category", "CampName", "VenueName", "SubmitterName",
                "LocationNote", "Date", "StartTime", "DurationMinutes", "IsRecurring", "PriorityRank", "Status", "SubmittedAt", "Host");
            foreach (var row in rows)
            {
                csv.WriteRow(row);
            }
        });
        await audit.LogAsync(EventsAuditActions.EventGuideExported, "EventGuide", settings?.Id ?? Guid.Empty,
            $"Approved event guide CSV exported: {rows.Count} rows", actorUserId);
        return bytes;
    }
}
