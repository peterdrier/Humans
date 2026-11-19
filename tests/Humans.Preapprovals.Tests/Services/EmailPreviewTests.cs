using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Preapprovals.Services;
using Humans.Preapprovals.Tests.Infrastructure;
using Humans.Testing;

namespace Humans.Preapprovals.Tests.Services;

public sealed class EmailPreviewTests
{
    private static IReadOnlyList<EmailPreviewSample> Samples() =>
        new PreapprovalsEmailPreviews(TestPreapprovalsEmails.CreateReal())
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"));

    [HumansFact]
    public void EveryNoticeKindHasAUniquePreviewSample()
    {
        var samples = Samples();
        samples.Should().HaveCount(Enum.GetValues<PreapprovalNoticeKind>().Length);
        samples.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    [HumansFact]
    public void AmountChangedSample_ShowsTheAmountBefore()
    {
        var sample = Samples().Single(s => string.Equals(s.Message.TemplateName, "preapproval_amount_changed", StringComparison.Ordinal));
        sample.Message.HtmlBody.Should().Contain("Before:</strong> 1,000.00 €");
    }
}
