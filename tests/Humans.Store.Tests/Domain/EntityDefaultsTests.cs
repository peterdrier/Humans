using AwesomeAssertions;
using Humans.Domain.Enums;
using Humans.Store.Contracts;
using Humans.Store.Domain;

namespace Humans.Store.Tests.Domain;

public class EntityDefaultsTests
{
    [HumansFact]
    public void New_order_defaults_to_open_state()
    {
        var o = new Order();

        o.State.Should().Be(OrderState.Open);
        o.Lines.Should().BeEmpty();
        o.Payments.Should().BeEmpty();
    }

    [HumansFact]
    public void New_product_defaults_to_active()
    {
        var p = new Product();

        p.IsActive.Should().BeTrue();
    }

    [HumansFact]
    public void Treasury_sync_state_singleton_id_is_one()
    {
        var s = new TreasurySyncState();

        s.Id.Should().Be(1);
        s.SyncStatus.Should().Be(TreasurySyncStatus.Idle);
    }
}
