using Humans.Base.Interfaces;
using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.Shifts.Tests;

public class SectionAccessMatrixTests
{
    private static AccessMatrixData Matrix() =>
        ((ISectionAccessMatrix)new Section()).AccessMatrices.Should().ContainSingle().Subject;

    [HumansFact]
    public void Contributes_the_Shifts_matrix_under_the_key_the_page_calls_it_with()
    {
        var matrix = Matrix();

        matrix.Key.Should().Be("Shifts");
        matrix.SectionName.Should().Be("Shifts");
        matrix.Order.Should().Be(10);
        matrix.Roles.Should().Equal("Volunteer", "Coordinator", "ShiftsAdmin");
    }

    [HumansFact]
    public void Pins_every_feature_row()
    {
        Matrix().Features
            .Select(f => $"{f.Name}: {string.Join(", ", f.RoleAccess.Select(kv => $"{kv.Key}={kv.Value}"))}")
            .Should().Equal(
                "Browse shifts: Volunteer=Allowed, Coordinator=Allowed, ShiftsAdmin=Allowed",
                "Sign up for shifts: Volunteer=Allowed, Coordinator=Allowed, ShiftsAdmin=Allowed",
                "My Shifts & availability: Volunteer=Allowed, Coordinator=Allowed, ShiftsAdmin=Allowed",
                "Create/edit rotas & shifts: Volunteer=Denied, Coordinator=Allowed, ShiftsAdmin=Allowed",
                "Approve/refuse signups: Volunteer=Denied, Coordinator=Allowed, ShiftsAdmin=Allowed",
                "Voluntell: Volunteer=Denied, Coordinator=Allowed, ShiftsAdmin=Allowed",
                "Staffing dashboard: Volunteer=Denied, Coordinator=Denied, ShiftsAdmin=Allowed");
    }
}
