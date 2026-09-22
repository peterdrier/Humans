using System.Globalization;
using Humans.GoogleIntegration.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.GoogleIntegration.Tests.Infrastructure;

/// <summary>
/// A real <see cref="GoogleIntegrationEmails"/> over a stub localizer — the builder is
/// sealed and has no interface, so callers assert on the <c>EmailMessage</c> it produces
/// rather than on a mock of it. Unknown keys render as <c>key#culture</c>, which keeps the
/// recipient's culture observable in the subject; body keys in <see cref="Formats"/>
/// render their format string so substitution can be asserted.
/// </summary>
internal static class TestGoogleIntegrationEmails
{
    private static readonly Dictionary<string, string> Formats = new(StringComparer.Ordinal)
    {
        ["GoogleIntegration_Email_WorkspaceCredentials_Body"] = "<p>{0}</p><p>{1}</p><p>{2}</p>",
        ["GoogleIntegration_Email_GoogleGroupRemoval_LossOfAccess_Body"] = "<p>{0}</p><p>{1}</p><p>{2}</p>",
        ["GoogleIntegration_Email_GoogleDriveRemoval_LossOfAccess_Body"] = "<p>{0}</p><p>{1}</p>",
        ["GoogleIntegration_Email_GoogleAccessRemoval_SecondaryCleanup_Body"] = "<p>{0}</p><p>{1}</p><p>{2}</p>",
    };

    public static GoogleIntegrationEmails Create(IReadOnlyDictionary<string, string>? formats = null)
    {
        formats ??= Formats;
        var localizer = Substitute.For<IStringLocalizer<GoogleIntegrationResource>>();
        localizer[Arg.Any<string>()].Returns(call =>
        {
            var key = call.Arg<string>();
            return new LocalizedString(key,
                formats.GetValueOrDefault(key, $"{key}#{CultureInfo.CurrentUICulture.Name}"));
        });

        return new GoogleIntegrationEmails(localizer, NullLogger<GoogleIntegrationEmails>.Instance);
    }
}
