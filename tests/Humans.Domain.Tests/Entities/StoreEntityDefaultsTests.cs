using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Domain.Tests.Entities;

public class StoreEntityDefaultsTests
{
    [HumansFact]
    public void New_order_defaults_to_open_state()
    {
        var o = new StoreOrder();

        o.State.Should().Be(StoreOrderState.Open);
        o.Lines.Should().BeEmpty();
        o.Payments.Should().BeEmpty();
    }

    [HumansFact]
    public void New_product_defaults_to_active()
    {
        var p = new StoreProduct();

        p.IsActive.Should().BeTrue();
    }

    [HumansFact]
    public void Treasury_sync_state_singleton_id_is_one()
    {
        var s = new StoreTreasurySyncState();

        s.Id.Should().Be(1);
        s.SyncStatus.Should().Be(StoreTreasurySyncStatus.Idle);
    }
}
