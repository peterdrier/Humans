using AwesomeAssertions;
using Humans.Store.Contracts;
using Humans.Store.Domain;

namespace Humans.Store.Tests.Domain;

public class EntityDefaultsTests
{
    [HumansFact]
    public void NewOrder_Defaults_IsOpen()
    {
        var o = new Order();

        o.State.Should().Be(OrderState.Open);
        o.Lines.Should().BeEmpty();
        o.Payments.Should().BeEmpty();
    }

    [HumansFact]
    public void NewProduct_Defaults_IsActive()
    {
        var p = new Product();

        p.IsActive.Should().BeTrue();
    }

    [HumansFact]
    public void NewPayment_Defaults_IsPaid()
    {
        var p = new Payment();

        p.Status.Should().Be(PaymentStatus.Paid);
        ((int)PaymentStatus.Paid).Should().Be(0);
    }
}
