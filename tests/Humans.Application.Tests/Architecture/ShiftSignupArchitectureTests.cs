using AwesomeAssertions;
using ShiftSignupService = Humans.Application.Services.Shifts.ShiftSignupService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for
/// <c>ShiftSignupService</c> (issue #541, sub-task b).
///
/// <para>
/// ShiftSignupService chose <b>Option A</b> (no caching decorator). Shift
/// signup reads are request-scoped — a user's own signups, a shift's pending
/// approvals — and don't benefit from a dict cache. Same rationale used by
/// Users (#243), Governance (#242), Feedback (#549), and Auth (#551) when
/// they skipped the decorator.
/// </para>
/// </summary>
public class ShiftSignupArchitectureTests
{
    // ── ShiftSignupService ──────────────────────────────────────────────────

    [HumansFact]
    public void ShiftSignupService_ConstructorTakesNoStoreType()
    {
        var ctor = typeof(ShiftSignupService).GetConstructors().Single();
        var storeParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal));

        storeParam.Should().BeNull(
            because: "Application services must not depend on store abstractions (design-rules §15); ShiftSignup Option A does not use a store at all");
    }

    [HumansFact]
    public void ShiftSignupService_IsSealed()
    {
        typeof(ShiftSignupService).IsSealed.Should().BeTrue(
            because: "Application-layer services are sealed to prevent ad-hoc extension; migrate behavior changes to the repository or a new service");
    }

}
