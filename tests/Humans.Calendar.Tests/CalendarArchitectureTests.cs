using AwesomeAssertions;
using Humans.Base.Caching;
using Humans.Base.Interfaces.Caching;
using Humans.Calendar.Domain;
using Humans.Calendar.Services;
using Humans.Calendar.Services.Dtos;

namespace Humans.Calendar.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Calendar.
/// </summary>
/// <remarks>
/// Two shape rules this file does NOT cover, so nothing here should be read as
/// covering them: that <c>CalendarService</c> never touches a <c>DbContext</c>, and
/// that the <c>ICalendarServiceRead</c> surface stays DTO-only. Both are review-time
/// rules; the section assembly holds the repository, so neither is a reference-graph
/// property any test can read off the assembly.
/// </remarks>
public class CalendarArchitectureTests
{
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
