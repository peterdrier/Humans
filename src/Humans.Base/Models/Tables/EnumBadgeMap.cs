using System.Collections.Concurrent;

using Humans.Base.Enums;

using Serilog;
using ILogger = Serilog.ILogger;

namespace Humans.Base.Models.Tables;

/// <summary>
/// Central enum-value → Bootstrap badge class registry for <see cref="CellFormat.EnumBadge"/> columns.
/// Views stop owning color decisions: add new mappings here, never inline in a view.
/// Unmapped values render as bg-secondary.
/// </summary>
/// <remarks>
/// A section owns its enums' rows and pushes them in from <c>Section.Register</c> via <see cref="Register"/>: Base
/// cannot name a section's enum, and referencing the contracts leaves would make Base reference every section
/// (<c>memory/architecture/base-ui-registries-are-section-populated.md</c>). The literal rows are enums still in Base.
/// </remarks>
public static class EnumBadgeMap
{
    // ConcurrentDictionary: parallel integration-test classes each compose their own host, so Section.Register calls
    // race against this shared static, writing while another host's requests read.
    private static readonly ConcurrentDictionary<Enum, string> Map = new()
    {
        [EmailOutboxStatus.Queued] = "bg-warning text-dark",
        [EmailOutboxStatus.Sent] = "bg-success",
        [EmailOutboxStatus.Failed] = "bg-danger",
    };

    // Static class, no DI: resolved per call so warnings reach the app's fully configured Serilog pipeline, not
    // whatever Log.Logger held at static init. Test-settable only.
    private static ILogger? LoggerOverride;

    private static ILogger Logger => LoggerOverride ?? Log.ForContext(typeof(EnumBadgeMap));

    internal static void SetLoggerForTests(ILogger value) => LoggerOverride = value;

    /// <summary>
    /// Adds a moved section's badge rows. Called from <c>ISection.Register</c>, so every write
    /// happens during <c>AddSections()</c> at composition time and the map is read-only by the
    /// first request — which is why a static suffices where <see cref="TableColumn{TRow}"/>'s
    /// render path has no DI to inject a registry through.
    /// </summary>
    /// <remarks>
    /// Idempotent per row, deliberately. A process composes the service collection more than
    /// once — <c>WebApplicationFactory</c> builds a host per integration-test class, and section
    /// architecture tests call <c>Register</c> against a throwaway <c>ServiceCollection</c> —
    /// while this map is static and outlives all of them. Re-registering a row with the same
    /// class is therefore normal and must be a no-op.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The value is already mapped to a <em>different</em> class. That is two owners disagreeing
    /// about one badge colour — a section-boundary bug — and letting the last registration win
    /// silently would hide it.
    /// </exception>
    public static void Register(IReadOnlyDictionary<Enum, string> rows)
    {
        foreach (var (value, cssClass) in rows)
        {
            // GetOrAdd makes check-and-add atomic; two hosts registering the same row
            // concurrently both see the winning value and agree.
            var existing = Map.GetOrAdd(value, cssClass);
            if (!string.Equals(existing, cssClass, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"{value.GetType().Name}.{value} is already mapped to '{existing}'; "
                    + $"cannot re-map it to '{cssClass}'.",
                    nameof(rows));
            }
        }
    }

    public static string For(Enum value)
    {
        if (Map.TryGetValue(value, out var cssClass))
            return cssClass;

        Logger.Warning(
            "EnumBadgeMap.For fell through to default for unhandled {EnumType} value '{Value}'",
            [value.GetType().Name, value]);
        return "bg-secondary";
    }
}
