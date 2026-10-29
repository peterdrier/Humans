using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Debug;

internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
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
}
