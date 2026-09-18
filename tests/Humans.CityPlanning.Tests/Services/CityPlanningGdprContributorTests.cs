using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Humans.CityPlanning.Services;
using Humans.Gdpr.Contracts;
using Xunit;

namespace Humans.CityPlanning.Tests.Services;

/// <summary>
/// City Planning's Article 15/17 account (nobodies-collective/Humans#1116). The only
/// user-scoped columns are the bare <c>LastModifiedByUserId</c>/<c>ModifiedByUserId</c>
/// attribution FKs, so the declaration is a retention rather than an erasure — see
/// <see cref="CityPlanningGdprContributor"/> for the reasoning.
/// </summary>
public class CityPlanningGdprContributorTests
{
    private const int MinimumReasonLength = 20;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [HumansFact]
    public void ErasureDeclaration_DeclaresCityPlanningEditsAsRetained()
    {
        var declaration = new CityPlanningGdprContributor().ErasureDeclaration;

        declaration.Should().ContainKey(GdprExportSections.CityPlanningEdits);
        var reason = declaration[GdprExportSections.CityPlanningEdits];
        reason.Should().NotBeNull();
        reason!.Trim().Length.Should().BeGreaterThanOrEqualTo(MinimumReasonLength);
    }

    [HumansFact]
    public void ErasureDeclaration_IsReadableOffAnUninitializedInstance()
    {
        // The coverage test (GdprErasureCoverageTests) reads this property off an instance
        // built with RuntimeHelpers.GetUninitializedObject — no constructor runs. If this
        // ever became an instance property instead of backed by a static field, it would
        // throw or return garbage here first.
        var uninitialized = (CityPlanningGdprContributor)RuntimeHelpers.GetUninitializedObject(
            typeof(CityPlanningGdprContributor));

        var declaration = uninitialized.ErasureDeclaration;

        declaration.Should().ContainKey(GdprExportSections.CityPlanningEdits);
    }

    [HumansFact]
    public async Task EraseForUserAsync_IsANoOp_IdempotentAcrossRepeatedCalls()
    {
        var sut = new CityPlanningGdprContributor();

        // No collaborators are injected, so any interaction beyond returning a completed
        // task would fail to compile/construct here — the assertion is that calling it
        // twice completes both times without throwing.
        await sut.EraseForUserAsync(Guid.NewGuid(), Ct);
        await sut.EraseForUserAsync(Guid.NewGuid(), Ct);
    }

    [HumansFact]
    public async Task ContributeForUserAsync_ReturnsNoRows()
    {
        // No read path exists for "polygons or history rows edited by this user" today, and
        // one is not added solely to populate an export slice (see the class doc comment).
        var sut = new CityPlanningGdprContributor();

        var slices = await sut.ContributeForUserAsync(Guid.NewGuid(), Ct);

        slices.Should().BeEmpty();
    }
}
