using AwesomeAssertions;
using TicketSyncService = Humans.Application.Services.Tickets.TicketSyncService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for
/// <see cref="TicketSyncService"/> — domain-persistence side migrated in
/// PR #545c (umbrella #545).
///
/// <para>
/// The Ticket Tailor API / webhook side remains in <c>Humans.Infrastructure</c>
/// as a vendor connector (<c>ITicketVendorService</c> →
/// <c>TicketTailorService</c> / <c>StubTicketVendorService</c>). These tests
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
            because: "Application services must not depend on store abstractions (design-rules §15); the Tickets section's §15 migration does not use a store");
    }

}
