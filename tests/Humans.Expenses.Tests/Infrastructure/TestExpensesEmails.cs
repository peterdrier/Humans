using System.Globalization;
using Humans.Base.Configuration;
using Humans.Expenses.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Expenses.Tests.Infrastructure;

/// <summary>
/// A real <see cref="ExpensesEmails"/> over a stub localizer — the builder is sealed and
/// has no interface, so callers assert on the <c>EmailMessage</c> it produces rather than on
/// a mock of it. Unknown keys render as <c>key#culture</c>, which keeps the recipient's
/// culture observable in the subject; keys in <see cref="Formats"/> render their format
/// string so substitution can be asserted.
/// </summary>
internal static class TestExpensesEmails
{
    public const string BaseUrl = "https://humans.test";

    private static readonly Dictionary<string, string> Formats = new(StringComparer.Ordinal)
    {
        ["Expenses_Email_Approved_Body"] =
            "<p>Hi {0},</p><p>Amount: {1}</p><p>IBAN: {2}</p><a href=\"{3}\">report</a>",
    };

    public static ExpensesEmails Create()
    {
        var localizer = Substitute.For<IStringLocalizer<ExpensesResource>>();
        localizer[Arg.Any<string>()].Returns(call =>
        {
            var key = call.Arg<string>();
            return new LocalizedString(key,
                Formats.GetValueOrDefault(key, $"{key}#{CultureInfo.CurrentUICulture.Name}"));
        });

        return new ExpensesEmails(
            Options.Create(new EmailSettings { BaseUrl = BaseUrl }),
            localizer,
            NullLogger<ExpensesEmails>.Instance);
    }
}
