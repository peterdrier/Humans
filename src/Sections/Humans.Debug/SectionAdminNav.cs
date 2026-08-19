using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Debug;

/// <summary>
/// Debug's admin sidebar contribution — the "Diagnostics" group (shared with Users) and the
/// "Design" group (nobodies-collective/Humans#1077).
/// </summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Diagnostics", System: true, Items: [
            new("Logs",          "Debug", "Logs",          null, null, "fa-solid fa-triangle-exclamation", PolicyNames.AdminOnly, Weight: 0),
            new("HTTP errors",   "Debug", "HttpErrors",    null, null, "fa-solid fa-circle-exclamation",   PolicyNames.AdminOnly, Weight: 10),
            new("DB stats",      "Debug", "DbStats",       null, null, "fa-solid fa-database",             PolicyNames.AdminOnly, Weight: 20),
            new("Cache stats",   "Debug", "CacheStats",    null, null, "fa-solid fa-bolt",                 PolicyNames.AdminOnly, Weight: 30),
            new("Client stats",  "Debug", "ClientStats",   null, null, "fa-solid fa-display",              PolicyNames.AdminOnly, Weight: 40),
            new("Timings",       "Debug", "Timings",       null, null, "fa-solid fa-stopwatch",            PolicyNames.AdminOnly, Weight: 50),
            new("Configuration", "Debug", "Configuration", null, null, "fa-solid fa-gear",                 PolicyNames.AdminOnly, Weight: 70),
            new("Maintenance",   "Debug", "Maintenance",   null, null, "fa-solid fa-screwdriver-wrench",   PolicyNames.AdminOnly, Weight: 80),
            new("Hangfire",      null,    null,            null, "/hangfire",     "fa-solid fa-clock-rotate-left", PolicyNames.AdminOnly, Weight: 90),
            new("Health",        null,    null,            null, "/health/ready", "fa-solid fa-heart-pulse",       PolicyNames.AdminOnly, Weight: 100)
        ], Weight: 140),
        new("Design", System: true, Items: [
            new("Color palette", "ColorPalette",  "Index", null, null, "fa-solid fa-palette", PolicyNames.AdminOnly),
            new("Components",    "WidgetGallery", "Index", null, null, "fa-solid fa-shapes",  PolicyNames.AdminOnly),
            new("Date formats",  "Debug",         "FormatGallery", null, null, "fa-solid fa-clock",    PolicyNames.AdminOnly),
            new("Translations",  "Debug",         "Translations",  null, null, "fa-solid fa-language", PolicyNames.AdminOnly)
        ], Weight: 160)
    ];
}
