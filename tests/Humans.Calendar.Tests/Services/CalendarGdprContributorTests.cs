using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Humans.Calendar.Services;
using Humans.Gdpr.Contracts;
using Xunit;

namespace Humans.Calendar.Tests.Services;

/// <summary>
/// Calendar's Article 15/17 account (nobodies-collective/Humans#1116). The only
/// user-scoped column on <c>CalendarEvent</c>/<c>CalendarEventException</c> is the bare
/// <c>CreatedByUserId</c> attribution FK, so the declaration is a retention rather than an
/// erasure — see <see cref="CalendarGdprContributor"/> for the reasoning.
/// </summary>
public class CalendarGdprContributorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [HumansFact]
    public void ErasureDeclaration_DeclaresCalendarEventsAsRetained()
    {
        var declaration = new CalendarGdprContributor().ErasureDeclaration;

        declaration.Should().ContainKey(GdprExportSections.CalendarEvents);
        declaration[GdprExportSections.CalendarEvents].Should().NotBeNull();
    }

    [HumansFact]
    public void ErasureDeclaration_IsReadableOffAnUninitializedInstance()
    {
        // The coverage test (GdprErasureCoverageTests) reads this property off an instance
        // built with RuntimeHelpers.GetUninitializedObject — no constructor runs. If this
        // ever became an instance property instead of backed by a static field, it would
        // throw or return garbage here first.
        var uninitialized = (CalendarGdprContributor)RuntimeHelpers.GetUninitializedObject(
            typeof(CalendarGdprContributor));

        var declaration = uninitialized.ErasureDeclaration;

        declaration.Should().ContainKey(GdprExportSections.CalendarEvents);
    }

    [HumansFact]
    public async Task EraseForUserAsync_IsANoOp_IdempotentAcrossRepeatedCalls()
    {
        var sut = new CalendarGdprContributor();

        // No collaborators are injected, so any interaction beyond returning a completed
        // task would fail to compile/construct here — the assertion is that calling it
        // twice completes both times without throwing.
        await sut.EraseForUserAsync(Guid.NewGuid(), Ct);
        await sut.EraseForUserAsync(Guid.NewGuid(), Ct);
    }

    [HumansFact]
    public async Task ContributeForUserAsync_ReturnsNoRows()
    {
        // No read path exists for "events created by this user" today, and one is not
        // added solely to populate an export slice (see the class doc comment).
        var sut = new CalendarGdprContributor();

        var slices = await sut.ContributeForUserAsync(Guid.NewGuid(), Ct);

        slices.Should().BeEmpty();
    }
}
