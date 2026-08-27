using Humans.Base.Controllers;
using Humans.Base.Extensions;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Gdpr.Controllers;

/// <summary>
/// Article 15 data export for profileless accounts (authenticated users without a
/// Profile). It lives here rather than beside the rest of the Guest dashboard — whose
/// frame is in Humans.Onboarding and whose comms and erasure actions are in
/// Humans.Users — because it calls Gdpr's own <see cref="IGdprExportService"/> and
/// nothing else.
/// </summary>
[Authorize]
internal sealed class GuestDataController(
    IUserServiceRead userService,
    IGdprExportService gdprExportService,
    IClock clock,
    ILogger<GuestDataController> logger) : HumansControllerBase(userService)
{
    private static readonly System.Text.Json.JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [HttpGet("Guest/DownloadData")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> DownloadData(CancellationToken ct)
    {
        var (noCurrentUser, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (noCurrentUser is not null)
            return noCurrentUser;

        try
        {
            var export = await gdprExportService.ExportForUserAsync(user.Id, ct);

            var payload = BuildExportPayload(export);
            var json = System.Text.Json.JsonSerializer.Serialize(payload, ExportJsonOptions);
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            var fileName = $"nobodies-data-export-{clock.GetCurrentInstant().ToDateTimeUtc().ToInvariantDate()}.json";

            return File(bytes, "application/json", fileName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to export data for user {UserId}", user.Id);
            SetError("Failed to export data. Please try again.");
            return RedirectToAction("Index", "Guest");
        }
    }

    private static Dictionary<string, object?> BuildExportPayload(GdprExport export)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ExportedAt"] = export.ExportedAt
        };
        foreach (var (section, data) in export.Sections)
        {
            payload[section] = data;
        }
        return payload;
    }
}
