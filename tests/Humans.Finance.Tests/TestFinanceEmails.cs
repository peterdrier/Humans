using System.Globalization;
using Humans.Finance.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Finance.Tests;

/// <summary>
/// A real <see cref="FinanceEmails"/> over a stub localizer — the builder is sealed and
/// has no interface, so callers assert on the <c>EmailMessage</c> it produces rather than on
/// a mock of it. Unknown keys render as <c>key#culture</c>, which keeps the recipient's
/// culture observable in the subject; keys in <see cref="Formats"/> render their format
/// string so substitution can be asserted.
/// </summary>
internal static class TestFinanceEmails
{
    private static readonly Dictionary<string, string> Formats = new(StringComparer.Ordinal)
    {
        ["Finance_Email_SepaPayoutGenerated_Body"] = "<p>Hi {0},</p><p>Amount: {1}</p><p>IBAN: {2}</p>",
    };

    public static FinanceEmails Create()
    {
        var localizer = Substitute.For<IStringLocalizer<FinanceResource>>();
        localizer[Arg.Any<string>()].Returns(call =>
        {
            var key = call.Arg<string>();
            return new LocalizedString(key,
                Formats.GetValueOrDefault(key, $"{key}#{CultureInfo.CurrentUICulture.Name}"));
        });

        return new FinanceEmails(localizer, NullLogger<FinanceEmails>.Instance);
    }
}
