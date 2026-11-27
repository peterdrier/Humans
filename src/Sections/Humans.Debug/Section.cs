using JetBrains.Annotations;
using System.Reflection;
using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Debug.Services;
using Humans.Debug.ViewComponents;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.AuditLog.Contracts;
using Humans.Base.Models;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;
using Humans.Debug.Controllers;

namespace Humans.Debug;

public sealed class Section : SectionBase, ISectionAdminNav, ISectionChrome, ISectionEndpoints, ISectionAccessMatrix
{
    IReadOnlyList<AccessMatrixData> ISectionAccessMatrix.AccessMatrices =>
    [
        new()
        {
            Key = "Debug",
            SectionName = "Debug",
            Order = 95,
            Roles = ["Board", "Admin"],
            Features =
            [
                Of("Configuration status", ("Board", Allowed), ("Admin", Allowed)),
                Of("Background jobs", ("Board", Limited), ("Admin", Allowed)),
            ]
        }
    ];

    public override void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, DebugAuditActions>();
        // Scoped: the migrations' delegates resolve the request's services.
        services.AddScoped<DataMigrationService>();
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        AdminGroup([
            AdminLink<DebugController>("Logs", nameof(DebugController.Logs), "fa-solid fa-triangle-exclamation", PolicyNames.BoardOrAdmin, weight: 0),
            AdminLink<DebugController>("HTTP errors", nameof(DebugController.HttpErrors), "fa-solid fa-circle-exclamation", PolicyNames.BoardOrAdmin, weight: 10),
            AdminLink<DebugController>("Database", nameof(DebugController.Database), "fa-solid fa-server", PolicyNames.BoardOrAdmin, weight: 15),
            AdminLink<DebugController>("DB stats", nameof(DebugController.DbStats), "fa-solid fa-database", PolicyNames.BoardOrAdmin, weight: 20),
            AdminLink<DebugController>("Cache stats", nameof(DebugController.CacheStats), "fa-solid fa-bolt", PolicyNames.BoardOrAdmin, weight: 30),
            AdminLink<DebugController>("Client stats", nameof(DebugController.ClientStats), "fa-solid fa-display", PolicyNames.BoardOrAdmin, weight: 40),
            AdminLink<DebugController>("Timings", nameof(DebugController.Timings), "fa-solid fa-stopwatch", PolicyNames.BoardOrAdmin, weight: 50),
            AdminLink<DebugController>("Sections", nameof(DebugController.Sections), "fa-solid fa-layer-group", PolicyNames.BoardOrAdmin, weight: 60),
            AdminLink<DebugController>("Configuration", nameof(DebugController.Configuration), "fa-solid fa-gear", PolicyNames.BoardOrAdmin, weight: 70),
            AdminLink<DebugController>("Maintenance", nameof(DebugController.Maintenance), "fa-solid fa-screwdriver-wrench", PolicyNames.BoardOrAdmin, weight: 80),
            AdminLink<UsersDebugController>("All users", nameof(UsersDebugController.Index), "fa-solid fa-bug-slash", PolicyNames.BoardOrAdmin, weight: 82),
            AdminLink<DataMigrationsController>("Data migrations", nameof(DataMigrationsController.Index), "fa-solid fa-right-left", PolicyNames.BoardOrAdmin, weight: 85),
            new("Hangfire",      null,    null,            null, "/hangfire",     "fa-solid fa-clock-rotate-left", PolicyNames.BoardOrAdmin, Weight: 90),
            new("Health",        null,    null,            null, "/health/ready", "fa-solid fa-heart-pulse",       PolicyNames.BoardOrAdmin, Weight: 100),
            AdminLink<ColorPaletteController>("Color palette", nameof(ColorPaletteController.Index), "fa-solid fa-palette", PolicyNames.BoardOrAdmin, weight: 110),
            AdminLink<WidgetGalleryController>("Components", nameof(WidgetGalleryController.Index), "fa-solid fa-shapes", PolicyNames.BoardOrAdmin, weight: 120),
            AdminLink<DebugController>("Date formats", nameof(DebugController.FormatGallery), "fa-solid fa-clock", PolicyNames.BoardOrAdmin, weight: 130),
            AdminLink<DebugController>("Translations", nameof(DebugController.Translations), "fa-solid fa-language", PolicyNames.BoardOrAdmin, weight: 140)
        ])
    ];

    IEnumerable<ChromeComponent> ISectionChrome.Components() =>
        [new(ChromeSlots.AdminDashboard, typeof(UserSetMembershipCardViewComponent), Weight: 5)];

    void ISectionEndpoints.MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/data-migrations", WriteStatusAsync).AllowAnonymous();

    internal static async Task WriteStatusAsync(HttpContext http)
    {
        var reports = await http.RequestServices.GetRequiredService<DataMigrationService>()
            .GetReportsAsync(http.RequestAborted);

        var response = new DataMigrationsStatusResponse(
            AppVersion(),
            [.. reports.OrderBy(r => r.Migration.Section, StringComparer.Ordinal)
                .ThenBy(r => r.Migration.Key, StringComparer.Ordinal)
                .Select(r => new DataMigrationProgress(r.Migration.Key, r.Status.Pending, r.Status.Done))]);

        await http.Response.WriteAsJsonAsync(response, http.RequestAborted);
    }

    /// <summary>The same <c>version</c> <c>/api/version</c> reports: the informational version without the commit.</summary>
    private static string AppVersion()
    {
        var informational = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? informational[..plus] : informational;
    }
}

[UsedImplicitly(ImplicitUseKindFlags.Access, ImplicitUseTargetFlags.WithMembers)]
internal sealed record DataMigrationsStatusResponse(string Version, IReadOnlyList<DataMigrationProgress> Migrations);

[UsedImplicitly(ImplicitUseKindFlags.Access, ImplicitUseTargetFlags.WithMembers)]
internal sealed record DataMigrationProgress(string Key, int Pending, bool Done);
