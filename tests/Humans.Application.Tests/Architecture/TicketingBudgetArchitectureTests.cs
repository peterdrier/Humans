using AwesomeAssertions;
using TicketingBudgetService = Humans.Application.Services.Tickets.TicketingBudgetService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 pattern for the Tickets→Budget bridge
/// service — migrated in PR #545b (umbrella #545).
///
/// <para>
/// <c>TicketingBudgetService</c> is a narrow bridge: reads paid ticket orders
/// through <see cref="ITicketingBudgetRepository"/> and delegates all
/// Budget-owned mutations (line items and projections) to
/// <see cref="IBudgetService"/>. No caching decorator — it is called from a
/// nightly batch job and a few admin-finance refresh buttons, nowhere close to
/// justifying a decorator.
/// </para>
/// </summary>
public class TicketingBudgetArchitectureTests
{
    // ── TicketingBudgetService ───────────────────────────────────────────────

    [HumansFact]
    public void TicketingBudgetService_ConstructorTakesNoStoreType()
    {
        var ctor = typeof(TicketingBudgetService).GetConstructors().Single();
        var storeParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal));

        storeParam.Should().BeNull(
            because: "the store pattern is retired (design-rules §15); §15 sections use in-decorator ConcurrentDictionary when caching is warranted, or no cache at all");
    }

}
