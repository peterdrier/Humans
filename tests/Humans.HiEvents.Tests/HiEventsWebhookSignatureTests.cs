using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Humans.HiEvents.Controllers;

namespace Humans.HiEvents.Tests;

public class HiEventsWebhookSignatureTests
{
    private const string Secret = "whsec";
    private const string OrderBody = """{"event_type":"order.updated","event_sent_at":"2027-01-02T10:00:00Z","payload":{"id":42,"event_id":7,"status":"COMPLETED"}}""";

    [HumansFact]
    public void IsValid_AcceptsHiEventsHmacOfTheBody()
    {
        HiEventsWebhookSignature.IsValid(OrderBody, Sign(OrderBody), Secret).Should().BeTrue();
    }

    [HumansFact]
    public void IsValid_RejectsATamperedBodyOrAnotherSecret()
    {
        HiEventsWebhookSignature.IsValid(OrderBody.Replace("42", "43", StringComparison.Ordinal), Sign(OrderBody), Secret).Should().BeFalse();
        HiEventsWebhookSignature.IsValid(OrderBody, Sign(OrderBody), "other").Should().BeFalse();
        HiEventsWebhookSignature.IsValid(OrderBody, "", Secret).Should().BeFalse();
    }

    [HumansFact]
    public void ReadOrder_ReadsOrderEventsOnly()
    {
        HiEventsWebhookSignature.ReadOrder(OrderBody).Should().Be((7, 42));
        HiEventsWebhookSignature.ReadOrder(OrderBody.Replace("order.updated", "attendee.updated", StringComparison.Ordinal)).Should().BeNull();
        HiEventsWebhookSignature.ReadOrder("not json").Should().BeNull();
    }

    private static string Sign(string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body)));
}
