using Humans.Base.Interfaces;
using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.Camps.Tests;

public class SectionAccessMatrixTests
{
    private static AccessMatrixData Matrix() =>
        ((ISectionAccessMatrix)new Section()).AccessMatrices.Should().ContainSingle().Subject;

    /// <summary>The key is "Camps" but the heading is "Barrios" — the user-facing word.</summary>
    [HumansFact]
    public void Contributes_the_Camps_matrix_headed_Barrios()
    {
        var matrix = Matrix();

        matrix.Key.Should().Be("Camps");
        matrix.SectionName.Should().Be("Barrios");
        matrix.Order.Should().Be(30);
        matrix.Roles.Should().Equal("Volunteer", "Camp Lead", "CampsAdmin");
    }

    [HumansFact]
    public void Pins_every_feature_row()
    {
        Matrix().Features
            .Select(f => $"{f.Name}: {string.Join(", ", f.RoleAccess.Select(kv => $"{kv.Key}={kv.Value}"))}")
            .Should().Equal(
                "Browse camps: Volunteer=Allowed, Camp Lead=Allowed, CampsAdmin=Allowed",
                "Register a camp: Volunteer=Allowed, Camp Lead=Allowed, CampsAdmin=Allowed",
                "Edit own camp: Volunteer=Denied, Camp Lead=Allowed, CampsAdmin=Allowed",
                "Approve/reject camps: Volunteer=Denied, Camp Lead=Denied, CampsAdmin=Allowed",
                "Camp settings: Volunteer=Denied, Camp Lead=Denied, CampsAdmin=Allowed");
    }
}
