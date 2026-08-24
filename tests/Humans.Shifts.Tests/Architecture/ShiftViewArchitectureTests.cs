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
/// (issue #720): inner / decorator placement, EF-reference boundaries,
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
    public void CachingShiftViewService_LivesInInfrastructureShifts()
    {
        typeof(CachingShiftViewService).Namespace
            .Should().Be("Humans.Shifts.Services",
                because: "caching decorators live in Infrastructure (mirrors CachingProfileService / CachingTeamService)");
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
                because: $"IShiftView methods return ValueTask<T> for cache-friendly async (issue #720) — '{method.Name}' returned '{rt.Name}'");
        }
    }

    [HumansFact]
    public void ShiftUserView_IsRecord()
    {
        // Record types compile to a sealed class with an EqualityContract property.
        typeof(ShiftUserView).GetProperty("EqualityContract",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Should().NotBeNull(because: "ShiftUserView is declared as a record");
    }

    [HumansFact]
    public void ShiftRotaView_IsRecord()
    {
        typeof(ShiftRotaView).GetProperty("EqualityContract",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Should().NotBeNull(because: "ShiftRotaView is declared as a record");
    }

    // ── Invalidator fan-in ──────────────────────────────────────────────────

    [HumansTheory]
    [MemberData(nameof(ShiftsServicesThatInvalidate))]
    public void Shifts_section_services_take_view_invalidator(Type serviceType)
    {
        var ctor = serviceType.GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IShiftViewInvalidator),
            because: "every Shifts-section service that mutates a row owned by the section must hold a reference to IShiftViewInvalidator (issue #720)");
    }
}
