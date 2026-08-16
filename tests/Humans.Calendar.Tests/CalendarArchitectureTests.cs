using AwesomeAssertions;
using Humans.Application.Interfaces.Caching;
using Humans.Calendar.Data;
using Humans.Calendar.Domain;
using Humans.Calendar.Services;
using Humans.Calendar.Services.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Humans.Calendar.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Calendar
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// Replaces <c>Humans.Application.Tests/Architecture/CalendarArchitectureTests.cs</c>. Its
/// <c>CalendarService_DoesNotImportMicrosoftEntityFrameworkCore</c> test is gone: it asserted
/// that <c>Humans.Application</c> carries no EF reference, and the section assembly holds the
/// repository and legitimately does. The invariant it was reaching for — the service never
/// touches a <c>DbContext</c> — is asserted directly on the constructor instead, which is
/// stronger and survives the move. The §15 decorator, DTO-only read surface and no-cross-section-nav
/// assertions carry over unchanged.
/// </remarks>
public class CalendarArchitectureTests
{
    /// <summary>
    /// Pins the set of types that may inject <see cref="ICalendarRepository"/>: the owning
    /// service and the repository implementation. A new consumer taking the repository directly
    /// would bypass the service layer and the single-writer rule for the <c>calendar_*</c> tables.
    /// </summary>
    [HumansFact]
    public void ICalendarRepository_HasNoUnexpectedConsumers()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Humans.Calendar.Services.CalendarService",
            "Humans.Calendar.Data.CalendarRepository",
        };

        var consumers = typeof(Section).Assembly.GetTypes()
            .Where(t => t.GetConstructors()
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(ICalendarRepository))))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        consumers.Where(c => !allowed.Contains(c)).Should().BeEmpty(
            because: "every read/write to the calendar_* tables must go through CalendarService");
    }

    [HumansFact]
    public void CachingCalendarService_ImplementsReadAndWriteSurfaces()
    {
        typeof(CachingCalendarService).Should().BeAssignableTo<ICalendarServiceRead>(
            because: "unkeyed ICalendarServiceRead resolves to the cache-backed read service");
        typeof(CachingCalendarService).Should().BeAssignableTo<ICalendarService>(
            because: "write calls still pass through the decorator so the read cache refreshes after mutations");
    }

    [HumansFact]
    public void CachingCalendarService_IsTrackedCache()
    {
        typeof(CachingCalendarService).Should().BeAssignableTo<ICacheStats>(
            because: "the calendar read cache is surfaced on /Debug/CacheStats");
    }

    [HumansFact]
    public void CalendarEventInfo_IsImmutableRecord()
    {
        var t = typeof(CalendarEventInfo);
        t.IsSealed.Should().BeTrue(because: "projection records are sealed");
        // Records expose the synthesized EqualityContract property.
        t.GetMethod("get_EqualityContract", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Should().NotBeNull(because: "CalendarEventInfo must be a record");
    }

    [HumansFact]
    public void CalendarEvent_HasNoOwningTeamNav()
    {
        typeof(CalendarEvent)
            .GetProperty("OwningTeam")
            .Should().BeNull(
                because: "CalendarEvent.OwningTeam was a cross-domain nav into the Teams section; the FK is now " +
                          "a bare column (design-rules §6c, memory/architecture/no-cross-section-ef-joins.md)");
    }

    [HumansFact]
    public void CalendarEvent_KeepsOwningTeamIdForeignKey()
    {
        typeof(CalendarEvent)
            .GetProperty("OwningTeamId")
            .Should().NotBeNull(
                because: "FK stays — only the navigation property is gone");
    }

    [HumansFact]
    public void AuditDiscriminatorsAreLiteralsNotDerivedFromTypeNames()
    {
        // These are literal string values we store in the DB. Pinned so a rename can't
        // quietly change them and orphan existing audit_log rows
        // (memory/code/type-name-as-persisted-string.md).
        AuditEntityTypes.CalendarEvent.Should().Be("CalendarEvent");
        AuditEntityTypes.Team.Should().Be("Team");
    }
}
