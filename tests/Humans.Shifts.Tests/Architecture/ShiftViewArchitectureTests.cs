using AwesomeAssertions;
using Humans.Shifts.Services.Dtos;
using Humans.Shifts.Services;
using Humans.Shifts.Contracts;
using Xunit;
using ShiftManagementService = Humans.Shifts.Services.ShiftManagementService;
using ShiftSignupService = Humans.Shifts.Services.ShiftSignupService;
using VolunteerTrackingService = Humans.Shifts.Services.VolunteerTrackingService;

namespace Humans.Shifts.Tests.Architecture;

public class ShiftViewArchitectureTests
{
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

    public static TheoryData<Type> ShiftsServicesThatInvalidate =>
    [
        typeof(ShiftSignupService),
        typeof(ShiftManagementService),
        typeof(VolunteerTrackingService)
    ];

    [HumansFact]
    public void CachingShiftViewService_LivesInShiftsServices()
    {
        typeof(CachingShiftViewService).Namespace
            .Should().Be("Humans.Shifts.Services",
                because: "caching decorators live alongside the section's other services (mirrors CachingProfileService / CachingTeamService)");
    }

    [HumansFact]
    public void CachingShiftViewService_ImplementsIShiftView()
    {
        typeof(CachingShiftViewService).Should().BeAssignableTo<IShiftView>();
    }

    [HumansFact]
    public void IShiftView_AllMethodsReturnValueTask()
    {
        // ValueTask<T> lets the decorator complete synchronously on dict hits (no Task
        // allocation, no thread hop) while still awaiting the load on a miss.
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

    [HumansTheory]
    [MemberData(nameof(ShiftsServicesThatInvalidate))]
    public void Shifts_section_services_take_view_cache(Type serviceType)
    {
        var ctor = serviceType.GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(CachingShiftViewService),
            because: "every Shifts-section service that mutates a row owned by the section must evict the view cache it changes (issue nobodies-collective/Humans#720)");
    }
}
