using AwesomeAssertions;
using Humans.Tickets.Services;
using Humans.Tickets.Data;

namespace Humans.Tickets.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for
/// <see cref="TicketSyncService"/> — domain-persistence side migrated in
/// PR #545c (umbrella #545).
///
/// <para>
/// The Ticket Tailor API side is its own section, <c>Humans.TicketTailor</c>: one
/// implementation of the Base port <c>ITicketVendorService</c>, deleted and replaced
/// wholesale when the vendor changes. These tests
/// pin the domain-persistence side's shape: Application-layer service, no
/// DbContext, all DB access via <c>ITicketRepository</c>, and cross-section
/// reads/writes routed through the owning services (not a second repo).
/// </para>
/// </summary>
public class TicketSyncArchitectureTests
{
    // ── TicketSyncService ────────────────────────────────────────────────────

    [HumansFact]
    public void TicketSyncService_ConstructorTakesNoStoreType()
    {
        var ctor = typeof(TicketSyncService).GetConstructors().Single();
        var storeParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal));

        storeParam.Should().BeNull(
            because: "section services must not depend on store abstractions (design-rules §15); Tickets does not use a store");
    }

}
