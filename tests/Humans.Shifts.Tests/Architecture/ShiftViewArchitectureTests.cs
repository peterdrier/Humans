using AwesomeAssertions;
using Humans.Shifts.Services.Dtos;
using Humans.Shifts.Services;
using Humans.Shifts.Contracts;
using Xunit;

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
}
