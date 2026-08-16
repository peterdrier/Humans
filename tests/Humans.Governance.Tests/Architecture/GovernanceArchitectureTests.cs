using System.Reflection;
using AwesomeAssertions;
using Humans.Governance.Data;
using Humans.Governance.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Humans.Governance.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the repository/service pattern for the Governance section.
/// Governance does not use a caching decorator or in-memory store — the service talks
/// directly to the repository and invalidates cross-cutting caches inline after successful
/// writes.
/// </summary>
public class GovernanceArchitectureTests
{
    [HumansFact]
    public void ApplicationDecisionService_TakesNoTypeFromInterfacesStoresNamespace()
    {
        var ctor = typeof(ApplicationDecisionService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => (p.ParameterType.Namespace ?? string.Empty)
                    .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal),
                because: "Governance has no store — service reads from IApplicationRepository directly (issue #533)");
    }

    /// <summary>
    /// Replaces the pre-G5 assertion that <c>typeof(ApplicationDecisionService).Assembly</c>
    /// carried no EF Core reference. That was a true statement about <c>Humans.Application</c>;
    /// the section assembly holds the repository and references EF on purpose, so the old
    /// assertion would either fail or — read as "does not contain" over a different assembly —
    /// keep passing while asserting nothing (nobodies-collective/Humans#866, Calendar's
    /// finding). The invariant it was reaching for is that the *service* never touches EF, and
    /// the constructor is where that shows.
    /// </summary>

    /// <summary>
    /// A section RCL's <c>_ViewImports</c> rebinds <c>Localizer</c> for every view in one line,
    /// so a view cannot read a carved key through the wrong set — but a controller that keeps
    /// taking a foreign <see cref="IStringLocalizer{T}"/> compiles and renders its carved keys
    /// as raw key names, usually on a validation path no render fixture reaches (§15.3b).
    /// Governance legitimately binds both markers: the tier-application form's copy and error
    /// messages are rendered by Shell's <c>ProfileController</c> too and stayed in
    /// <c>SharedResource</c>.
    /// </summary>
}
