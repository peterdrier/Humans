using AwesomeAssertions;
using Humans.Shifts.Services.Dtos;
using Humans.Shifts.Services;
using Humans.Shifts.Contracts;
using Xunit;
using ShiftManagementService = Humans.Shifts.Services.ShiftManagementService;
using ShiftSignupService = Humans.Shifts.Services.ShiftSignupService;
using VolunteerTrackingService = Humans.Shifts.Services.VolunteerTrackingService;

namespace Humans.Shifts.Tests.Architecture;

/// <summary>
/// Architecture tests for the cached <see cref="IShiftView"/> surface
/// (issue nobodies-collective/Humans#720): inner / decorator placement, EF-reference boundaries,
/// invalidator fan-in at every Shifts-section service.
/// </summary>
public class ShiftViewArchitectureTests
{
    public static TheoryData<Type> ShiftsServicesThatInvalidate =>
    [
        typeof(ShiftSignupService),
        typeof(ShiftManagementService),
        typeof(VolunteerTrackingService)
    ];

    // ── CachingShiftViewService (Singleton decorator) ────────────────────────

    [HumansFact]
    public void CachingShiftViewService_LivesInShiftsServices()
    {
        typeof(CachingShiftViewService).Namespace
            .Should().Be("Humans.Shifts.Services",
                because: "caching decorators live alongside the section's other services (mirrors CachingProfileService / CachingTeamService)");
    }

    [HumansFact]
    public void CachingShiftViewService_ImplementsIShiftViewAndInvalidator()
    {
        typeof(CachingShiftViewService).Should().BeAssignableTo<IShiftView>();
        typeof(CachingShiftViewService).Should().BeAssignableTo<IShiftViewInvalidator>();
    }

    // ── IShiftView contract ──────────────────────────────────────────────────

    [HumansFact]
    public void IShiftView_AllMethodsReturnValueTask()
    {
        // ValueTask<T> lets the decorator complete synchronously on dict hits
        // (no Task allocation, no thread hop) while still supporting the
        // awaiting load path on miss. Mirrors IProfileService.GetFullProfileAsync.
        var methods = typeof(IShiftView).GetMethods();
        foreach (var method in methods)
        {
            var rt = method.ReturnType;
            var isValueTaskOfT = rt.IsGenericType
                && rt.GetGenericTypeDefinition() == typeof(ValueTask<>);

            isValueTaskOfT.Should().BeTrue(
                because: $"IShiftView methods return ValueTask<T> for cache-friendly async (issue nobodies-collective/Humans#720) — '{method.Name}' returned '{rt.Name}'");
        }
    }

    // ── Invalidator fan-in ──────────────────────────────────────────────────

    [HumansTheory]
    [MemberData(nameof(ShiftsServicesThatInvalidate))]
    public void Shifts_section_services_take_view_invalidator(Type serviceType)
    {
        var ctor = serviceType.GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IShiftViewInvalidator),
            because: "every Shifts-section service that mutates a row owned by the section must hold a reference to IShiftViewInvalidator (issue nobodies-collective/Humans#720)");
    }
}
