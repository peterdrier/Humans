using AwesomeAssertions;
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
    public void New_payment_defaults_to_paid()
    {
        // The column default was dropped after AddStorePaymentStatus ran, so this initializer is
        // the only thing that settles a sync or manual insert. Paid is also the enum's zero member.
        var p = new Payment();

        p.Status.Should().Be(PaymentStatus.Paid);
        ((int)PaymentStatus.Paid).Should().Be(0);
    }

    [HumansFact]
    public void Treasury_sync_state_singleton_id_is_one()
    {
        var s = new TreasurySyncState();

        s.Id.Should().Be(1);
        s.SyncStatus.Should().Be(TreasurySyncStatus.Idle);
    }
}
