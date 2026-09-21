using System.Globalization;
using Humans.Shifts.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Shifts.Tests.Infrastructure;

/// <summary>
/// A real <see cref="ShiftsEmails"/> over a stub localizer — the builder is sealed and has
/// no interface, so callers assert on the <c>EmailMessage</c> it produces rather than on a
/// mock of it. Unknown keys render as <c>key#culture</c>, which keeps the recipient's
/// culture observable in the subject; keys in <see cref="Formats"/> render their format
/// string so substitution can be asserted.
/// </summary>
internal static class TestShiftsEmails
{
    private static readonly Dictionary<string, string> Formats = new(StringComparer.Ordinal)
    {
        ["Shifts_Email_CoordinatorRotaMessage_Body"] = "<p>Dear {0},</p><p>From {1} on {2}:</p>{3}{4}{5}",
        ["Shifts_Email_CoordinatorTeamRotasMessage_Body"] = "<p>Dear {0},</p><p>From {1} on {2}:</p>{3}{4}{5}",
    };

    public static ShiftsEmails Create()
    {
        var localizer = Substitute.For<IStringLocalizer<ShiftsResource>>();
        localizer[Arg.Any<string>()].Returns(call =>
        {
            var key = call.Arg<string>();
            return new LocalizedString(key,
                Formats.GetValueOrDefault(key, $"{key}#{CultureInfo.CurrentUICulture.Name}"));
        });

        return new ShiftsEmails(localizer, NullLogger<ShiftsEmails>.Instance);
    }
}
