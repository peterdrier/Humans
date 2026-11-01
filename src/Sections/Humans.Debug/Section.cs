using System.Reflection;
using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Debug.Services;
using Humans.Debug.ViewComponents;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.AuditLog.Contracts;

namespace Humans.Debug;

public sealed class Section : ISection, ISectionAdminNav, ISectionChrome, ISectionEndpoints
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, DebugAuditActions>();
        // Scoped: the migrations' delegates resolve the request's services.
        services.AddScoped<DataMigrationService>();
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Debug", [
            new("Logs",          "Debug", "Logs",          null, null, "fa-solid fa-triangle-exclamation", PolicyNames.BoardOrAdmin, Weight: 0),
            new("HTTP errors",   "Debug", "HttpErrors",    null, null, "fa-solid fa-circle-exclamation",   PolicyNames.BoardOrAdmin, Weight: 10),
            new("DB stats",      "Debug", "DbStats",       null, null, "fa-solid fa-database",             PolicyNames.BoardOrAdmin, Weight: 20),
            new("Cache stats",   "Debug", "CacheStats",    null, null, "fa-solid fa-bolt",                 PolicyNames.BoardOrAdmin, Weight: 30),
            new("Client stats",  "Debug", "ClientStats",   null, null, "fa-solid fa-display",              PolicyNames.BoardOrAdmin, Weight: 40),
            new("Timings",       "Debug", "Timings",       null, null, "fa-solid fa-stopwatch",            PolicyNames.BoardOrAdmin, Weight: 50),
            new("Sections",      "Debug", "Sections",      null, null, "fa-solid fa-layer-group",          PolicyNames.BoardOrAdmin, Weight: 60),
            new("Configuration", "Debug", "Configuration", null, null, "fa-solid fa-gear",                 PolicyNames.BoardOrAdmin, Weight: 70),
            new("Maintenance",   "Debug", "Maintenance",   null, null, "fa-solid fa-screwdriver-wrench",   PolicyNames.BoardOrAdmin, Weight: 80),
            new("All users", "UsersDebug", "Index", null, null, "fa-solid fa-bug-slash", PolicyNames.BoardOrAdmin, Weight: 82),
            new("Data migrations", "DataMigrations", "Index", null, null, "fa-solid fa-right-left",         PolicyNames.BoardOrAdmin, Weight: 85),
            new("Hangfire",      null,    null,            null, "/hangfire",     "fa-solid fa-clock-rotate-left", PolicyNames.BoardOrAdmin, Weight: 90),
            new("Health",        null,    null,            null, "/health/ready", "fa-solid fa-heart-pulse",       PolicyNames.BoardOrAdmin, Weight: 100),
            new("Color palette", "ColorPalette",  "Index",         null, null, "fa-solid fa-palette",  PolicyNames.BoardOrAdmin, Weight: 110),
            new("Components",    "WidgetGallery", "Index",         null, null, "fa-solid fa-shapes",   PolicyNames.BoardOrAdmin, Weight: 120),
            new("Date formats",  "Debug",         "FormatGallery", null, null, "fa-solid fa-clock",    PolicyNames.BoardOrAdmin, Weight: 130),
            new("Translations",  "Debug",         "Translations",  null, null, "fa-solid fa-language", PolicyNames.BoardOrAdmin, Weight: 140)
        ])
    ];

    IEnumerable<ChromeComponent> ISectionChrome.Components() =>
        [new(ChromeSlots.AdminDashboard, typeof(UserSetMembershipCardViewComponent), Weight: 5)];

    void ISectionEndpoints.MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/data-migrations", (RequestDelegate)WriteStatusAsync).AllowAnonymous();

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

internal sealed record DataMigrationsStatusResponse(string Version, IReadOnlyList<DataMigrationProgress> Migrations);

internal sealed record DataMigrationProgress(string Key, int Pending, bool Done);
